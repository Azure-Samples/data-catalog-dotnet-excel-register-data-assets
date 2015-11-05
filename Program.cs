//Microsoft Data Catalog team sample

using System;
using Microsoft.IdentityModel.Clients.ActiveDirectory;
using System.Net;
using System.IO;
using System.Data;
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
        static AuthenticationResult authResult = AccessToken();
        static string sampleRootPath = string.Empty;

        static RetryPolicy retryPolicy;

        static void Main(string[] args)
        {
            DirectoryInfo di = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            sampleRootPath = di.Parent.Parent.FullName;

            //Register the data source container
            string jsonContainerTemplate = new StreamReader(string.Format(@"{0}\AdHocContainerSample.json", sampleRootPath)).ReadToEnd();
            string jsonContainerPayload = string.Format(jsonContainerTemplate, DateTime.UtcNow);

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
        static AuthenticationResult AccessToken()
        {
            if (authResult == null)
            {
                //Resource Uri for Data Catalog API
                string resourceUri = "https://datacatalog.azure.com";

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
                authResult = authContext.AcquireToken(resourceUri, clientId, new Uri(redirectUri), PromptBehavior.RefreshSession);
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
                jsonAssetPayload = string.Format(jsonAssetTemplate, containerId, name, DateTime.UtcNow);

                //Use the container id returned from the registration of the container to register the asset.
                //To register a data asset, use "tables" as view type
                string assetId = RegisterDataAsset(catalogName, jsonAssetPayload, "tables");
                Console.WriteLine(string.Format("Data Asset Registered: {0} - {1}", name, assetId));

                //Annotate Data Asset with the Description column
                string descriptionJson = DescriptionJson(description, "user1@contoso.com", DateTime.UtcNow.ToString());
                //Annotate a description
                AnnotateDataAsset(catalogName, assetId, "descriptions", descriptionJson);

                Console.WriteLine(string.Format("Data Asset Description Annotated: {0}", assetId));
            }
        }


        //  Register data asset:
        // The Register Data Asset operation registers a new data asset 
        // or updates an existing one if an asset with the same identity already exists.
        // To register a data asset:
        //  1. Register a data source container. viewType = containers
        //  2. Register a data asset using a container id. viewType = tables
        static string RegisterDataAsset(string catalogName, string json, string viewType)
        {
            string location = string.Empty;
            string publishResultStatus = string.Empty;

            //Get access token to use to call operation
            authResult = AccessToken();

            string fullUri = string.Format("https://{0}.datacatalog.azure.com/{1}/views/{2}?api-version=2015-07.1.0-Preview",
                authResult.TenantId, catalogName, viewType);

            //Create a POST WebRequest as a Json content type
            HttpWebRequest request = System.Net.WebRequest.Create(fullUri) as System.Net.HttpWebRequest;
            request.KeepAlive = true;
            request.Method = "POST";
            request.ContentLength = 0;
            request.ContentType = "application/json";

            //To authorize the operation call, you need an access token which is part of the Authorization header
            request.Headers.Add("Authorization", authResult.CreateAuthorizationHeader());

            //Add a guid to help with diagnostics
            string guid = Guid.NewGuid().ToString();
            request.Headers.Add("x-ms-client-request-id", guid);

            //POST web request
            byte[] byteArray = System.Text.Encoding.UTF8.GetBytes(json);
            request.ContentLength = byteArray.Length;

            //Write JSON byte[] into a Stream and get web response
            using (Stream writer = request.GetRequestStream())
            {
                writer.Write(byteArray, 0, byteArray.Length);

                try
                {
                    using (var httpWebResponse = (HttpWebResponse)retryPolicy.ExecuteAction(() => (request.GetResponse())))
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
            }

            return location;
        }

        //Annotate Data Asset:
        // The Annotate Data Asset operation annotates an asset.
        static string AnnotateDataAsset(string catalogName, string viewType, string jsonNode, string json)
        {
            string responseString = string.Empty;

            //Get access token to use to call operation
            authResult = AccessToken();

            string fullUri = string.Format("https://{0}.datacatalog.azure.com/{1}/views/{2}/{3}?api-version=2015-07.1.0-Preview",
                authResult.TenantId, catalogName, viewType, jsonNode);

            //Create a POST WebRequest as a Json content type
            HttpWebRequest request = System.Net.WebRequest.Create(fullUri) as System.Net.HttpWebRequest;
            request.KeepAlive = true;
            request.Method = "POST";
            request.ContentLength = 0;
            request.ContentType = "application/json";

            //To authorize the operation call, you need an access token which is part of the Authorization header
            request.Headers.Add("Authorization", authResult.CreateAuthorizationHeader());

            //Add a guid to help with diagnostics
            string guid = Guid.NewGuid().ToString();
            request.Headers.Add("x-ms-client-request-id", guid);

            //POST web request
            byte[] byteArray = System.Text.Encoding.UTF8.GetBytes(json);
            request.ContentLength = byteArray.Length;

            //Write JSON byte[] into a Stream and get web response
            using (Stream writer = request.GetRequestStream())
            {
                writer.Write(byteArray, 0, byteArray.Length);

                try
                {
                    using (var httpWebResponse = (HttpWebResponse)retryPolicy.ExecuteAction(() => (request.GetResponse())))
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
            }

            return responseString;
        }

        // Description JSON
        static string DescriptionJson(string description, string creatorId, string modifiedTime)
        {
            return "{" +
                "\"description\": \"" + description + "\"," +
                "\"__creatorId\": \"" + creatorId + "\"," +
                "\"modifiedTime\": \"" + modifiedTime + "\"" +
                "}";
        }
    }
}