/*
* Function: AzureSpeechAnalyzer
* Created: 4050 B.C.
* Created By: Ra
* Description:
     Generates a transcript for an input audio or video file using Azure Speech Analyzer (formerly Azure Media Indexer v2 Preview) media processor.
* Usage:
  1) Call the function app with the "GET" verb and an "inputuri" querystring specifying the URI to a media file that is to be analyzed.
     The input URI can be a SAS URL of a file in a private Azure storage blob, the URL of a file in a public Azure Storage blob (not recommended),
     or a similar pointing to a file.
     * Optional: Call the function with the POST verb and with inputuri and specify a custom configuration JSON payload in the request body.
  2) The function creates a job to process. It returns the job id in the response body.

* How It Works
  1) Deploy the function using the Azure Resource Manager (ARM) deployment template:
     https://github.com/StratusOn/MSI-Depot/azuredeploy.json
     This also deploys the GetToken function and enables MSI (https://github.com/StratusOn/MSI-GetToken-FunctionApp/azuredeploy.json).
  2) The deployment creates a new Azure Media Services (AMS) account in the resource group as well as a new Azure Storage account for the AMS account.
  2) The deployment creates the following application settings:
     * MSI_GETTOKEN_ENDPOINT: Used by this function to retrieve a JWT auth token.
     * MEDIASERVICES_ACCOUNT_ENDPOINT: Used by the function to create assets and process jobs.
     * MEDIASERVICES_STORAGEACCOUNT_CONNECTIONSTRING: Stores AMS assets and metadata.

* NOTE:
    There is another version of the deployment template that deploys the GetToken function along with a warmup timer function to keep the GetToken calls super quick.
    https://github.com/StratusOn/MSI-Depot/azuredeploy-with-warmup.json
    * That "warmup" version sets the GetToken function timeout to the 10 minutes maximum and configures a timer function to make a call every 10 minutes.
    * The warmup function is a naive implementation that does not account for scaleout (i.e. does not attempt to call each function app instance).
    * If the GetToken function is called so many times that it is resulting in scaleout on the consumption plan, then a better option might be to move the 
      GetToken function to a Basic (or better) App Service Plan.
*/
#r "Microsoft.WindowsAzure.Storage"
#r "Newtonsoft.Json"
#r "System.Web"

using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using Newtonsoft.Json;

private const string IndexerJobConfiguration =
    "{\r\n    \"Features\":\r\n    [\r\n        {\r\n            \"Options\":\r\n            {\r\n                \"Formats\":\r\n                [\r\n                    \"WebVtt\",\r\n                    \"Sami\"\r\n                ],\r\n                \"Language\": \"EnUs\",\r\n                \"Type\": \"RecoOptions\",\r\n                \"GenerateLattice\": \"True\",\r\n                \"CaptionStyle\":\r\n                {\r\n                    \"Mode\": \"Rollover\",\r\n                    \"MaxLines\": 1,\r\n                    \"TruncateLongWords\": true,\r\n                    \"MaxCharsPerLine\": 70,\r\n                    \"MaxLineDuration\": \"00:00:08\"\r\n               }\r\n            },\r\n            \"Type\": \"SpReco\"\r\n        }\r\n    ],\r\n    \"Version\": 1.0\r\n}\r\n";

private const string TaskBody =
    "<?xml version=\"1.0\" encoding=\"utf-8\"?><taskBody><inputAsset>JobInputAsset(0)</inputAsset><outputAsset assetCreationOptions=\"0\" assetName=\"{0}\">JobOutputAsset(0)</outputAsset></taskBody>";

private static readonly Dictionary<string, string> RequiredMediaServicesHeaders =
    new Dictionary<string, string>()
    {
        { "x-ms-version", "2.15" },
        { "DataServiceVersion", "3.0" },
        { "MaxDataServiceVersion", "3.0" },
        { "Accept", "application/json" }
    };

private static readonly Dictionary<string, string> RequiredMediaServicesHeadersWithMetadata =
    new Dictionary<string, string>()
    {
        { "x-ms-version", "2.15" },
        { "DataServiceVersion", "3.0" },
        { "MaxDataServiceVersion", "3.0" },
        { "Accept", "application/json;odata=verbose" }
    };

public static async Task<HttpResponseMessage> Run(HttpRequestMessage req, TraceWriter log)
{
    log.Info("C# HTTP trigger function processed a request.");

    // Do nothing if this a warmup call.
    if (req.GetQueryNameValuePairs().Any(q => string.Compare(q.Key, "iswarmup", true) == 0))
    {
        log.Info("Processed a warmup request.");
        return new HttpResponseMessage(HttpStatusCode.OK);
    }

    string mediaServicesAccountEndpoint = ConfigurationManager.AppSettings["MEDIASERVICES_ACCOUNT_ENDPOINT"];
    if (string.IsNullOrWhiteSpace(mediaServicesAccountEndpoint))
    {
        return new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("The MEDIASERVICES_ACCOUNT_ENDPOINT application setting is missing.", Encoding.UTF8, "application/json")
        };
    }
    log.Info($"Media Services Account Endpoint: {mediaServicesAccountEndpoint}");

    string mediaServicesStorageAccountConnectionString = ConfigurationManager.AppSettings["MEDIASERVICES_STORAGEACCOUNT_CONNECTIONSTRING"];
    if (string.IsNullOrWhiteSpace(mediaServicesStorageAccountConnectionString))
    {
        return new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("The MEDIASERVICES_STORAGEACCOUNT_CONNECTIONSTRING application setting is missing.", Encoding.UTF8, "application/json")
        };
    }
    log.Info($"Media Services Storage Account Connection String: {mediaServicesStorageAccountConnectionString}");

    string msiGetTokenEndpoint = ConfigurationManager.AppSettings["MSI_GETTOKEN_ENDPOINT"];
    if (string.IsNullOrWhiteSpace(msiGetTokenEndpoint))
    {
        return new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("The MSI_GETTOKEN_ENDPOINT application setting is missing.", Encoding.UTF8, "application/json")
        };
    }
    log.Info($"MSI GetToken Endpoiint: {msiGetTokenEndpoint}");

    string inputUrl = req.GetQueryNameValuePairs().FirstOrDefault(q => string.Compare(q.Key, "inputuri", true) == 0).Value;
    if (string.IsNullOrWhiteSpace(inputUrl))
    {
        return new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("The inputuri querystring parameter must be specified and with a non-empty value.", Encoding.UTF8, "application/json")
        };
    }

    string configuration = IndexerJobConfiguration;
    // Get configuration from request body if POST:
    if (req.Method == HttpMethod.Post)
    {
        string data = await req.Content.ReadAsAsync<string>();
        if (!string.IsNullOrWhiteSpace(data))
        {
            configuration = data;
        }
    }

    try
    {
        int suffix = new Random().Next(10000000, 99999999);

        // 1) Get a fresh token.
        string token = await InvokeRestMethodAsync(msiGetTokenEndpoint, log, HttpMethod.Get);

        // 2) Create an input asset.
        string inputAssetName = $"InputAsset-{suffix}";
        string assetOutput = await CreateMediaServicesAssetAsync(mediaServicesAccountEndpoint, inputAssetName, token, log);
        var inputAsset = JsonConvert.DeserializeObject<Asset>(assetOutput);

        // 3) Copy input media into asset's storage account container.
        string containerName = inputAsset.Uri.Substring(inputAsset.Uri.LastIndexOf("/", StringComparison.InvariantCulture) + 1);
        Uri sourceUri = new Uri(inputUrl);
        string blobName = sourceUri.LocalPath.Substring(sourceUri.LocalPath.LastIndexOf("/", StringComparison.InvariantCulture) + 1);
        await CopyInputBlobIntoAssetAsync(mediaServicesStorageAccountConnectionString, sourceUri, containerName, blobName, log);

        // 4) Create the input asset file.
        await CreateMediaServicesAssetFileInfos(mediaServicesAccountEndpoint, inputAsset, token, log);

        // 5) Create a job for Indexer v2.
        string outputAssetName = $"OutputAsset-{suffix}";
        string jobName = $"Indexer2-Job-{suffix}";
        string taskName = $"Indexer2-JobTask-{suffix}";
        var jobTask = new JobTask()
        {
            Name = taskName,
            Configuration = configuration,
            MediaProcessorId = MediaProcessors.MediaIndexer2,
            TaskBody = string.Format(TaskBody, outputAssetName)
        };
        string jobOutput = await CreateMediaServicesJobAsync(mediaServicesAccountEndpoint, jobName, inputAsset, token, jobTask, log);

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(jobOutput, Encoding.UTF8, "application/json")
        };
    }
    catch (Exception ex)
    {
        log.Error($"Indexing failed: {ex.Message}");

        return new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent($"Indexing failed with error message: {ex.Message}", Encoding.UTF8, "application/json")
        };
    }
}

private static async Task<string> CreateMediaServicesAssetAsync(string mediaServicesAccountEndpoint, string assetName, string accessToken, TraceWriter log)
{
    string createAssetUrl = $"{mediaServicesAccountEndpoint}Assets";
    string content = JsonConvert.SerializeObject(new Asset() { Name = assetName }, new JsonSerializerSettings() { NullValueHandling = NullValueHandling.Ignore });
    var asset = await InvokeRestMethodAsync(createAssetUrl, log, HttpMethod.Post, content, accessToken, headers: RequiredMediaServicesHeaders);
    log.Info($"asset: {asset}");
    return asset;
}

private static async Task<string> CreateMediaServicesAssetFileInfos(string mediaServicesAccountEndpoint, Asset asset, string accessToken, TraceWriter log)
{
    string createAssetInfosUrl = $"{mediaServicesAccountEndpoint}CreateFileInfos?assetid='{asset.Id}'";
    var assetInfos = await InvokeRestMethodAsync(createAssetInfosUrl, log, HttpMethod.Get, null, accessToken, headers: RequiredMediaServicesHeaders);
    log.Info($"asset infos: {assetInfos}");
    return assetInfos;
}

private static async Task<string> CreateMediaServicesJobAsync(string mediaServicesAccountEndpoint, string jobName, Asset inputAsset, string accessToken, JobTask jobTask, TraceWriter log)
{
    string createJobUrl = $"{mediaServicesAccountEndpoint}Jobs";
    string inputAssetUrl = $"{mediaServicesAccountEndpoint}Assets('{inputAsset.Id}')";
    var job = new Job()
    {
        Name = jobName,
        InputMediaAssets = new[] { new MediAasset() { __metadata = new AssetMetadata() { uri = inputAssetUrl } } },
        Tasks = new[] { jobTask }
    };

    string content = JsonConvert.SerializeObject(job, new JsonSerializerSettings() { NullValueHandling = NullValueHandling.Ignore });
    var jobOutput = await InvokeRestMethodAsync(createJobUrl, log, HttpMethod.Post, content, accessToken, headers: RequiredMediaServicesHeaders, additionalContentTypeHeaders: ";odata=verbose");
    log.Info($"job: {jobOutput}");
    return jobOutput;
}

private static async Task<CloudStorageAccount> CopyInputBlobIntoAssetAsync(string mediaServicesStorageAccountConnectionString, Uri sourceUri, string assetContainerName, string blobName, TraceWriter log)
{
    log.Info($"Container Name: {assetContainerName}");
    log.Info($"Blob Name: {blobName}");

    // Retrieve storage account from connection string.
    CloudStorageAccount storageAccount = CloudStorageAccount.Parse(mediaServicesStorageAccountConnectionString);
    log.Info($"Storage Account: {storageAccount.BlobEndpoint.Host}");

    // Create the blob client.
    CloudBlobClient blobClient = storageAccount.CreateCloudBlobClient();
    CloudBlobContainer container = blobClient.GetContainerReference(assetContainerName);
    await container.CreateIfNotExistsAsync();
    CloudBlockBlob blob = container.GetBlockBlobReference(blobName);
    string localFile = await DownloadSourceFileFromInputUrl(sourceUri, log);

    try
    {
        await blob.UploadFromFileAsync(localFile);
    }
    finally
    {
        if (File.Exists(localFile))
        {
            File.Delete(localFile);
        }
    }

    return storageAccount;
}

private static async Task<string> DownloadSourceFileFromInputUrl(Uri inputUri, TraceWriter log)
{
    using (var webClient = new WebClient())
    {
        string fileName = $"{new Random().Next(10000000, 99999999)}";
        string localFile = Path.Combine(Path.GetTempPath(), fileName);
        log.Info($"Local File: {localFile}");

        await webClient.DownloadFileTaskAsync(inputUri, localFile);
        return localFile;
    }
}

private static async Task<string> InvokeRestMethodAsync(string url, TraceWriter log, HttpMethod httpMethod, string body = null, string authorizationToken = null, string authorizationScheme = "Bearer", IDictionary<string, string> headers = null, string additionalContentTypeHeaders = "")
{
    HttpClient client = new HttpClient();
    if (!string.IsNullOrWhiteSpace(authorizationToken))
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(authorizationScheme, authorizationToken);
        log.Info($"Authorization: {client.DefaultRequestHeaders.Authorization.Parameter}");
    }

    HttpRequestMessage request = new HttpRequestMessage(httpMethod, url);
    if (headers != null && headers.Count > 0)
    {
        foreach (var header in headers)
        {
            request.Headers.Add(header.Key, header.Value);
        }
    }

    if (!string.IsNullOrWhiteSpace(body))
    {
        request.Content = new StringContent(body, Encoding.UTF8);
        if (!string.IsNullOrWhiteSpace(additionalContentTypeHeaders))
        {
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse($"application/json{additionalContentTypeHeaders}");
        }
        else
        {
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/json");
        }
    }

    HttpResponseMessage response = await client.SendAsync(request);
    if (response.IsSuccessStatusCode)
    {
        return await response.Content.ReadAsStringAsync();
    }

    string statusCodeName = response.StatusCode.ToString();
    int statusCodeValue = (int)response.StatusCode;
    string content = await response.Content.ReadAsStringAsync();
    log.Info($"Status Code: {statusCodeName} ({statusCodeValue}). Body: {content}");

    throw new Exception($"Status Code: {statusCodeName} ({statusCodeValue}). Body: {content}");
}

public class Asset
{
    public string Name { get; set; }
    public string Id { get; set; }
    public string Uri { get; set; }
}

public class Job
{
    public string Name { get; set; }
    public MediAasset[] InputMediaAssets { get; set; }
    public MediAasset[] OutputMediaAssets { get; set; }
    public JobTask[] Tasks { get; set; }
}

public class MediAasset
{
    public AssetMetadata __metadata { get; set; }
}

public class AssetMetadata
{
    public string uri { get; set; }
}

public class JobTask
{
    public string Name { get; set; }
    public string Configuration { get; set; }
    public string MediaProcessorId { get; set; }
    public string TaskBody { get; set; }
}

public class MediaProcessors
{
    public const string MediaIndexer2 = "nb:mpid:UUID:1927f26d-0aa5-4ca1-95a3-1a3f95b0f706";
}
