// --------------------------------------------------------------------
// <copyright>
//   Copyright (c) Microsoft Corporation.  All rights reserved.
// </copyright>
// --------------------------------------------------------------------

using Microsoft.Practices.EnterpriseLibrary.TransientFaultHandling;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Data.Services.Client;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace ConsoleApplication.Utilities
{
    /// <summary>
    /// Provides the transient error detection logic that can recognize transient faults when dealing with Windows Azure storage services.
    /// </summary>
    internal class HttpRequestTransientErrorDetectionStrategy : ITransientErrorDetectionStrategy
    {
        private delegate bool IsTransientDelegate(Exception ex);

        private static readonly IsTransientDelegate[] _transientDelegates = new IsTransientDelegate[]
        {
            WebIsTransient,
            SocketIsTransient,
            DataServiceRequestIsTransient,
            DataServiceClientIsTransient,
            IoExeptionIsTransient
        };

        /// <summary>
        /// The error code strings that will be used to check for transient errors.
        /// </summary>
        private static readonly ReadOnlyCollection<string> TransientStorageErrorCodeStrings = new List<string>
        {
            "InternalError",
            "ServerBusy",
            "OperationTimedOut",
            "TableServerOutOfMemory",
            "TableBeingDeleted"
        }.AsReadOnly();

        private static readonly Regex _errorCodeRegex = new Regex(@"<code>(\w+)</code>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly int[] _httpStatusCodes = new[]
        {
            (int)HttpStatusCode.InternalServerError,
            (int)HttpStatusCode.GatewayTimeout,
            (int)HttpStatusCode.ServiceUnavailable,
            (int)HttpStatusCode.RequestTimeout
        };

        private static readonly WebExceptionStatus[] _webExceptionStatus = new[]
        {
            WebExceptionStatus.ConnectionClosed,
            WebExceptionStatus.Timeout,
            WebExceptionStatus.RequestCanceled,
            WebExceptionStatus.KeepAliveFailure,
            WebExceptionStatus.PipelineFailure,
            WebExceptionStatus.ReceiveFailure,
            WebExceptionStatus.ConnectFailure,
            WebExceptionStatus.SendFailure
        };

        private static readonly SocketError[] _socketErrorCodes = new[]
        {
            SocketError.ConnectionRefused,
            SocketError.TimedOut
        };

        /// <summary>
        /// Determines whether the specified exception represents a transient failure that can be compensated by a retry.
        /// </summary>
        /// <param name="ex">The exception object to be verified.</param>
        /// <returns>true if the specified exception is considered transient; otherwise, false.</returns>
        public bool IsTransient(Exception ex)
        {
            return ex != null && (this.CheckIsTransient(ex) || (ex.InnerException != null && this.CheckIsTransient(ex.InnerException)));
        }

        /// <summary>
        /// Checks whether the specified exception is transient.
        /// </summary>
        /// <param name="ex">The exception object to be verified.</param>
        /// <returns>true if the specified exception is considered transient; otherwise, false.</returns>
        protected virtual bool CheckIsTransient(Exception ex)
        {
            bool isTransient = false;
            for (int i = 0; !isTransient && i < _transientDelegates.Length; i++)
            {
                isTransient = _transientDelegates[i](ex);
            }
            return isTransient;
        }

        /// <summary>
        /// Gets the error code string from the exception.
        /// </summary>
        /// <param name="ex">The exception that contains the error code as a string inside the message.</param>
        /// <returns>The error code string.</returns>
        protected static string GetErrorCode(DataServiceRequestException ex)
        {
            string value = null;
            if (ex != null && ex.InnerException != null)
            {
                var match = _errorCodeRegex.Match(ex.InnerException.Message);

                value = match.Groups[1].Value;
            }
            return value;
        }

        private static bool IoExeptionIsTransient(Exception ex)
        { 
            // This may be System.IO.IOException: "Unable to read data from the transport connection: The connection was closed" which could manifest itself under extremely high load.
            // Do not return true if ex is a subtype of IOException (such as FileLoadException, when it cannot load a required assembly)
            return (ex.GetType() == typeof(IOException) && ex.InnerException == null);
        }

        private static bool WebIsTransient(Exception ex)
        {
            bool isTransient = false;
            var webException = ex as WebException;
            if (webException != null)
            {
                var response = webException.Response as HttpWebResponse;
                isTransient = (_webExceptionStatus.Contains(webException.Status))
                              || ((webException.Status == WebExceptionStatus.ProtocolError)
                                  && (response != null && _httpStatusCodes.Contains((int)response.StatusCode)));
            }

            return isTransient;
        }

        private static bool SocketIsTransient(Exception ex)
        {
            var socketException = ex as SocketException;

            // This section handles the following transient faults:
            //
            // Exception Type: System.Net.Sockets.SocketException
            //  Error Code: 10061
            //      Message: No connection could be made because the target machine actively refused it XXX.XXX.XXX.XXX:443
            //      Socket Error Code: ConnectionRefused
            //  Exception Type: System.Net.Sockets.SocketException
            //      Error Code: 10060
            //      Message: A connection attempt failed because the connected party did not properly respond after a period of time, or established connection failed because connected host has failed to respond 168.62.128.143:443
            //      Socket Error Code: TimedOut
            return (socketException != null) && _socketErrorCodes.Contains(socketException.SocketErrorCode);
        }

        private static bool DataServiceRequestIsTransient(Exception ex)
        {
            bool isTransient = false;
            var dataServiceException = ex as DataServiceRequestException;
            if (dataServiceException != null)
            {
                DataServiceResponse response = dataServiceException.Response;
                isTransient = (TransientStorageErrorCodeStrings.Contains(GetErrorCode(dataServiceException))
                               || (response != null && response.Any(x => _httpStatusCodes.Contains(x.StatusCode))));
            }
            return isTransient;
        }

        private static bool DataServiceClientIsTransient(Exception ex)
        {
            var dataServiceClientException = ex as DataServiceClientException;

            // It was found that sometimes a connection can be subject to unexpected termination with a SocketException. 
            // The WCF Data Services client may not include actual exception but report it inside the message text, for example, the error message can say:
            // "System.Net.WebException: The underlying connection was closed: A connection that was expected to be kept alive was closed by the server. ---> 
            // System.IO.IOException: Unable to read data from the transport connection: A connection attempt failed because the connected party did not properly respond 
            // after a period of time, or established connection failed because connected host has failed to respond. ---> 
            // System.Net.Sockets.SocketException: A connection attempt failed because the connected party did not properly respond after a period of time, or 
            // established connection failed because connected host has failed to respond".
            // It was also found that the above exception may have a status code of 500 (Internal Server Error).

            return ((dataServiceClientException != null) && _httpStatusCodes.Contains(dataServiceClientException.StatusCode));
        }
    }
}
