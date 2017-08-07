//Microsoft Data Catalog team sample

using System;
using System.Text;
using Microsoft.IdentityModel.Clients.ActiveDirectory;
using System.Net;
using System.IO;
using System.Data;
using System.ServiceModel;
using System.Threading.Tasks;
using GetStartedADCExtensions;

//Install-Package EnterpriseLibrary.TransientFaultHandling
using Microsoft.Practices.EnterpriseLibrary.TransientFaultHandling;
using ConsoleApplication.Utilities;

namespace ConsoleApplication
{
    class Program
    {
        //TODO: Change {ClientID} to your app client ID
        static string clientIDFromAzureAppRegistration = "{ClientID}";

        //Note: To find the Catalog name, sign into Azure Data Catalog, and choose User. You will see the Catalog name.
        static string catalogName = "DefaultCatalog";
        static AuthenticationResult authResult = AccessToken().Result;
        static string sampleRootPath = string.Empty;

        static RetryPolicy retryPolicy;

        static void Main(string[] args)
        {
            DirectoryInfo di = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            sampleRootPath = di.Parent.Parent.FullName;
            string upn = authResult.UserInfo.DisplayableId;

            //Register the data source container
            string jsonContainerTemplate = new StreamReader(string.Format(@"{0}\AdHocContainerSample.json", sampleRootPath)).ReadToEnd();
            string jsonContainerPayload = string.Format(jsonContainerTemplate, upn);

            retryPolicy = new RetryPolicy(
                new HttpRequestTransientErrorDetectionStrategy(),
                5,
                TimeSpan.FromMilliseconds(100),
                TimeSpan.FromMilliseconds(500)
                );

            //To register a container, use "containers" as view type
            string containerId = RegisterDataAsset(catalogName, jsonContainerPayload, "containers");

            RegisterDataAssets(containerId);

            Console.WriteLine();
            Console.WriteLine("Data assets registered from Excel table. Press Enter");
            Console.ReadLine();
        }

        //Get access token:
        // To call a Data Catalog REST operation, create an instance of AuthenticationContext and call AcquireToken
        // AuthenticationContext is part of the Active Directory Authentication Library NuGet package
        // To install the Active Directory Authentication Library NuGet package in Visual Studio, 
        //  run "Install-Package Microsoft.IdentityModel.Clients.ActiveDirectory" from the NuGet Package Manager Console.
        static async Task<AuthenticationResult> AccessToken()
        {
            if (authResult == null)
            {
                //Resource Uri for Data Catalog API
                string resourceUri = "https://api.azuredatacatalog.com";

                //To learn how to register a client app and get a Client ID, see https://msdn.microsoft.com/en-us/library/azure/mt403303.aspx#clientID   
                string clientId = clientIDFromAzureAppRegistration;

                //A redirect uri gives AAD more details about the specific application that it will authenticate.
                //Since a client app does not have an external service to redirect to, this Uri is the standard placeholder for a client app.
                string redirectUri = "https://login.live.com/oauth20_desktop.srf";

                // Create an instance of AuthenticationContext to acquire an Azure access token
                // OAuth2 authority Uri
                string authorityUri = "https://login.windows.net/common/oauth2/authorize";
                AuthenticationContext authContext = new AuthenticationContext(authorityUri);

                // Call AcquireToken to get an Azure token from Azure Active Directory token issuance endpoint
                //  AcquireToken takes a Client Id that Azure AD creates when you register your client app.
                authResult = await authContext.AcquireTokenAsync(resourceUri, clientId, new Uri(redirectUri), new PlatformParameters(PromptBehavior.Always));
            }

            return authResult;
        }

        //Register data assets from an Excel table
        static void RegisterDataAssets(string containerId)
        {
            string name = string.Empty;
            string description = string.Empty;

            //Get the Excel workbook path, Sheet Name, and Table Name
            DirectoryInfo di = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            string path = String.Format(@"{0}\Ad Hoc Data Catalog.xlsx", di.Parent.Parent.FullName);
            string sheetName = "AdventureWorks2014";
            string tableName = "Table1";

            //ExcelTableToDataTable() is an Extension method which converts an Excel Table to a Data Table
            DataTable dt = new DataTable();
            dt.ExcelTableToDataTable(path, sheetName, tableName);

            //Get the Data Asset JSON template
            string jsonAssetTemplate = new StreamReader(string.Format(@"{0}\AdHocSample.json", sampleRootPath)).ReadToEnd();
            string jsonAssetPayload = string.Empty;

            //Get all DataTable Rows
            DataRowCollection rows = dt.Rows;

            //Register each table from the Excel Workbook 
            foreach (DataRow row in rows)
            {
                name = row["Table"].ToString();
                description = row["Description"].ToString();

                //Create the JSON payload from Table column
                jsonAssetPayload = string.Format(jsonAssetTemplate, containerId, name, authResult.UserInfo.DisplayableId);

                //Use the container id returned from the registration of the container to register the asset.
                //To register a data asset, use "tables" as view type
                string assetUrl = RegisterDataAsset(catalogName, jsonAssetPayload, "tables");
                Console.WriteLine("Data Asset Registered: {0} - {1}", name, assetUrl);

                //Annotate a description
                AnnotateDataAsset(assetUrl, "descriptions", DescriptionJson(description));

                Console.WriteLine("Data Asset Description Annotated: {0}", assetUrl);
            }
        }


        //  Register data asset:
        // The Register Data Asset operation registers a new data asset 
        // or updates an existing one if an asset with the same identity already exists.
        // To register a data asset:
        //  1. Register a data source container. viewType = containers
        //  2. Register a data asset using a container id. viewType = tables
        private static string RegisterDataAsset(string catalogName, string json, string viewType)
        {
            string location = string.Empty;
            string publishResultStatus = string.Empty;

            string fullUri = string.Format("https://api.azuredatacatalog.com/catalogs/{0}/views/{1}?api-version=2016-03-30", catalogName, viewType);

            //Create a POST WebRequest as a Json content type
            HttpWebRequest request = System.Net.WebRequest.Create(fullUri) as System.Net.HttpWebRequest;
            request.KeepAlive = true;
            request.Method = "POST";
            try
            {
                using (var httpWebResponse = retryPolicy.ExecuteAction(() => SetRequestAndGetResponse(request, json)))
                {
                    publishResultStatus = httpWebResponse.StatusDescription;

                    //Get the Response header which contains the data asset ID
                    //The format is: tables/{data asset ID} 
                    location = httpWebResponse.Headers["Location"];
                }
            }
            catch (WebException ex)
            {
                Console.WriteLine(ex.Message);
                Console.WriteLine(ex.Status);
                if (ex.Response != null)
                {
                    // can use ex.Response.Status, .StatusDescription
                    if (ex.Response.ContentLength != 0)
                    {
                        using (var stream = ex.Response.GetResponseStream())
                        {
                            using (var reader = new StreamReader(stream))
                            {
                                Console.WriteLine(reader.ReadToEnd());
                            }
                        }
                    }
                }
                location = null;
            }

            return location;
        }

        //Annotate Data Asset:
        // The Annotate Data Asset operation annotates an asset.
        private static string AnnotateDataAsset(string viewUrl, string nestedViewName, string json)
        {
            string responseString = string.Empty;

            string fullUri = string.Format("{0}/{1}?api-version=2016-03-30", viewUrl, nestedViewName);

            //Create a POST WebRequest as a Json content type
            HttpWebRequest request = System.Net.WebRequest.Create(fullUri) as System.Net.HttpWebRequest;
            request.KeepAlive = true;
            request.Method = "POST";
            try
            {
                using (var httpWebResponse = retryPolicy.ExecuteAction(() => SetRequestAndGetResponse(request, json)))
                {
                    StreamReader reader = new StreamReader(httpWebResponse.GetResponseStream());
                    responseString = reader.ReadToEnd();
                }
            }
            catch (WebException ex)
            {
                Console.WriteLine(ex.Message);
                Console.WriteLine(ex.Status);
                if (ex.Response != null)
                {
                    // can use ex.Response.Status, .StatusDescription
                    if (ex.Response.ContentLength != 0)
                    {
                        using (var stream = ex.Response.GetResponseStream())
                        {
                            using (var reader = new StreamReader(stream))
                            {
                                Console.WriteLine(reader.ReadToEnd());
                            }
                        }
                    }
                }
                responseString = null;
            }

            return responseString;
        }

        static HttpWebResponse SetRequestAndGetResponse(HttpWebRequest request, string payload = null)
        {
            while (true)
            {
                //Add a guid to help with diagnostics
                string guid = Guid.NewGuid().ToString();
                request.Headers.Add("x-ms-client-request-id", guid);
                //To authorize the operation call, you need an access token which is part of the Authorization header
                request.Headers.Add("Authorization", AccessToken().Result.CreateAuthorizationHeader());
                //Set to false to be able to intercept redirects
                request.AllowAutoRedirect = false;

                if (!string.IsNullOrEmpty(payload))
                {
                    byte[] byteArray = Encoding.UTF8.GetBytes(payload);
                    request.ContentLength = byteArray.Length;
                    request.ContentType = "application/json";
                    //Write JSON byte[] into a Stream
                    request.GetRequestStream().Write(byteArray, 0, byteArray.Length);
                }
                else
                {
                    request.ContentLength = 0;
                }

                HttpWebResponse response = request.GetResponse() as HttpWebResponse;

                // Requests to **Azure Data Catalog (ADC)** may return an HTTP 302 response to indicate
                // redirection to a different endpoint. In response to a 302, the caller must re-issue
                // the request to the URL specified by the Location response header. 
                if (response.StatusCode == HttpStatusCode.Redirect)
                {
                    string redirectedUrl = response.Headers["Location"];
                    HttpWebRequest nextRequest = WebRequest.Create(redirectedUrl) as HttpWebRequest;
                    nextRequest.Method = request.Method;
                    request = nextRequest;
                }
                else
                {
                    return response;
                }
            }
        }

        // Description JSON
        private static string DescriptionJson(string description)
        {
            return string.Format(@"
{{
    ""properties"" : {{
        ""key"": ""{0}"",
        ""fromSourceSystem"": false,
        ""description"": ""{1}""
    }}
}}
", Guid.NewGuid().ToString("N"), description);
        }
    }
}