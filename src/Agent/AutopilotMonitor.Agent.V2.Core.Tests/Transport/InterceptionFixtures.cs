using AutopilotMonitor.Agent.V2.Core.Transport;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Transport
{
    /// <summary>
    /// Synthetic copies of the pages an agent gets instead of a backend answer: the Azure platform
    /// page when no client certificate arrived, the platform page of a stopped app, and a proxy's
    /// block page. Rebuilt from the observed structure and wording; none of them is a captured
    /// artifact. The style block keeps the platform marker text past the first 200 characters, where
    /// the old upload log excerpt ended.
    /// </summary>
    internal static class InterceptionFixtures
    {
        public const string PlatformPageTitle = "Web App - Unavailable";
        public const string ProxyPageTitle = "Example Gateway - Notification";
        public const string InspectionIssuer = "CN=Example Inspection CA, O=Example Security Ltd, C=US";

        private const string PlatformPageHead =
            "<!DOCTYPE html PUBLIC \"-//W3C//DTD XHTML 1.0 Strict//EN\" \"http://www.w3.org/TR/xhtml1/DTD/xhtml1-strict.dtd\">\r\n" +
            "<html xmlns=\"http://www.w3.org/1999/xhtml\">\r\n<head>\r\n<title>Web App - Unavailable</title>\r\n" +
            "<style type=\"text/css\">\r\n  html { height: 100%; width: 100%; }\r\n" +
            "  #content { margin: 0 auto; padding: 40px 0 0; font-family: \"Segoe UI\", Arial, sans-serif; }\r\n" +
            "  h3 { color: #3a3a3a; font-size: 26px; font-weight: 300; }\r\n</style>\r\n</head>\r\n<body>\r\n<div id=\"content\">\r\n";

        public const string CertificateRequiredPage =
            PlatformPageHead +
            "<h3>Error 403 - Forbidden: Client Certificate Required.</h3>\r\n" +
            "<p>The web app you have attempted to reach requires a client certificate and does not accept any requests without one.</p>\r\n" +
            "</div>\r\n</body>\r\n</html>\r\n";

        public const string StoppedAppPage =
            PlatformPageHead +
            "<h3>Error 403 - This web app is stopped.</h3>\r\n" +
            "<p>The web app you have attempted to reach is currently stopped and does not accept any requests.</p>\r\n" +
            "</div>\r\n</body>\r\n</html>\r\n";

        public const string ProxyPage =
            "<!DOCTYPE html>\r\n<html><head><meta charset=\"utf-8\"><title>Example Gateway - Notification</title></head>\r\n" +
            "<body><h1>Access blocked</h1><p>The request was blocked by the security policy of your organization.</p></body></html>\r\n";

        /// <summary>The platform's "Client Certificate Required" page seen through an inspecting proxy.</summary>
        public static ResponseClassification CertificateRequired(int statusCode = 403) =>
            ResponseClassification.ForForeign(statusCode, ResponseClassification.ClientCertificateRequired, PlatformPageTitle, isHtml: true, InspectionIssuer);

        public static ResponseClassification StoppedApp(int statusCode = 403) =>
            ResponseClassification.ForPlatformStopped(statusCode, PlatformPageTitle, isHtml: true, serverCertIssuer: null);
    }
}
