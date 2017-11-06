# MSI-Depot
A collection of Azure Managed Service Identity (MSI) samples.

## Deploying the Azure Media Services Samples
To deploy the AMS samples (first sample is Indexer V2), click on the following button:

[![Deploy to Azure](http://azuredeploy.net/deploybutton.png)](https://portal.azure.com/#create/Microsoft.Template/uri/https%3A%2F%2Fraw.githubusercontent.com%2FStratusOn%2FMSI-Depot%2Fmaster%2FMSI-Samples%2FDeployments%2Fazuredeploy.json)

Once deployed:
1. Copy the AMS Indexer V2 endpoint URL from the "azureSpeechAnalyzerEndpoint" output property from the template deployment.
2. Add a URL-encoded URL pointing to a video or audio file to be processed by Media Indexer V2.
