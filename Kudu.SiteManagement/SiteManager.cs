﻿using System;
using System.IO;
using System.Net.NetworkInformation;
using System.Threading;
using Kudu.Core.Infrastructure;
using IIS = Microsoft.Web.Administration;

namespace Kudu.SiteManagement
{
    public class SiteManager : ISiteManager
    {
        private const string KuduAppPoolName = "kudu";
        private static Random portNumberGenRnd = new Random((int)DateTime.Now.Ticks);

        private readonly IPathResolver _pathResolver;

        public SiteManager(IPathResolver pathResolver)
        {
            _pathResolver = pathResolver;
        }

        public Site CreateSite(string applicationName)
        {
            var iis = new IIS.ServerManager();

            try
            {
                // Create the service site for this site
                string serviceSiteName = GetServiceSite(applicationName);
                int serviceSitePort = CreateSite(iis, serviceSiteName, _pathResolver.ServiceSitePath);

                // Create the main site
                string siteName = GetLiveSite(applicationName);
                string siteRoot = _pathResolver.GetLiveSitePath(applicationName);
                string webRoot = Path.Combine(siteRoot, Constants.WebRoot);

                FileSystemHelpers.EnsureDirectory(webRoot);
                File.WriteAllText(Path.Combine(webRoot, "index.html"), @"<html> 
<head>
<title>The web site is under construction</title>
<style type=""text/css"">
 BODY { color: #444444; background-color: #E5F2FF; font-family: verdana; margin: 0px; text-align: center; margin-top: 100px; }
 H1 { font-size: 16pt; margin-bottom: 4px; }
</style>
</head>
<body>
<h1>The web site is under construction</h1><br/>
</body> 
</html>");

                int sitePort = CreateSite(iis, siteName, webRoot);

                // Map a path called app to the site root under the service site
                MapServiceSitePath(iis, applicationName, Constants.MappedLiveSite, siteRoot);

                // Commit the changes to iis
                iis.CommitChanges();

                // Give IIS some time to create the site and map the path
                // REVIEW: Should we poll the site's state?
                Thread.Sleep(1000);

                return new Site
                {
                    ServiceUrl = String.Format("http://localhost:{0}/", serviceSitePort),
                    SiteUrl = String.Format("http://localhost:{0}/", sitePort),
                };
            }
            catch
            {
                DeleteSite(applicationName);
                throw;
            }
        }

        public bool TryCreateDeveloperSite(string applicationName, out string siteUrl)
        {
            var iis = new IIS.ServerManager();

            string devSiteName = GetDevSite(applicationName);

            IIS.Site site = iis.Sites[devSiteName];
            if (site == null)
            {
                // Get the path to the dev site
                string siteRoot = _pathResolver.GetDeveloperApplicationPath(applicationName);
                string webRoot = Path.Combine(siteRoot, Constants.WebRoot);
                int sitePort = CreateSite(iis, devSiteName, webRoot);

                // Ensure the directory is created
                FileSystemHelpers.EnsureDirectory(webRoot);

                // Map a path called app to the site root under the service site
                MapServiceSitePath(iis, applicationName, Constants.MappedDevSite, siteRoot);

                iis.CommitChanges();


                siteUrl = String.Format("http://localhost:{0}/", sitePort);
                return true;
            }

            siteUrl = null;
            return false;
        }

        public void DeleteSite(string applicationName)
        {
            var iis = new IIS.ServerManager();

            var kuduPool = EnsureKuduAppPool(iis);

            DeleteSite(iis, GetLiveSite(applicationName));
            DeleteSite(iis, GetDevSite(applicationName));
            // Don't delete the physical files for the service site
            DeleteSite(iis, GetServiceSite(applicationName), deletePhysicalFiles: false);

            iis.CommitChanges();

            string appPath = _pathResolver.GetApplicationPath(applicationName);
            var sitePath = _pathResolver.GetLiveSitePath(applicationName);
            var devPath = _pathResolver.GetDeveloperApplicationPath(applicationName);

            try
            {
                kuduPool.StopAndWait();

                DeleteSafe(sitePath);
                DeleteSafe(devPath);
                DeleteSafe(appPath);
            }
            finally
            {
                kuduPool.StartAndWait();
            }

        }

        public void SetDeveloperSiteWebRoot(string applicationName, string siteRoot)
        {
            var iis = new IIS.ServerManager();
            string siteName = GetDevSite(applicationName);

            IIS.Site site = iis.Sites[siteName];
            if (site != null)
            {
                string devSitePath = _pathResolver.GetDeveloperApplicationPath(applicationName);
                string webRoot = Path.Combine(devSitePath, Constants.WebRoot, siteRoot);

                // Change the web root
                site.Applications[0].VirtualDirectories[0].PhysicalPath = webRoot;

                iis.CommitChanges();

                Thread.Sleep(1000);
            }
        }

        private static void MapServiceSitePath(IIS.ServerManager iis, string applicationName, string path, string siteRoot)
        {
            string serviceSiteName = GetServiceSite(applicationName);

            // Get the service site
            IIS.Site site = iis.Sites[serviceSiteName];
            if (site == null)
            {
                throw new InvalidOperationException("Could not retrieve service site");
            }

            // Map the path to the live site in the service site
            site.Applications.Add(path, siteRoot);
        }

        private static IIS.ApplicationPool EnsureKuduAppPool(IIS.ServerManager iis)
        {
            var kuduAppPool = iis.ApplicationPools[KuduAppPoolName];
            if (kuduAppPool == null)
            {
                iis.ApplicationPools.Add(KuduAppPoolName);
                iis.CommitChanges();
                kuduAppPool = iis.ApplicationPools[KuduAppPoolName];
                kuduAppPool.Enable32BitAppOnWin64 = true;
                kuduAppPool.ManagedPipelineMode = IIS.ManagedPipelineMode.Integrated;
                kuduAppPool.ManagedRuntimeVersion = "v4.0";
                kuduAppPool.AutoStart = true;
                kuduAppPool.WaitForState(IIS.ObjectState.Started);
            }

            return kuduAppPool;
        }

        //TODO this is duplicated in HgServer.cs, though out of sync in functionality.
        private int GetRandomPort(IIS.ServerManager iis)
        {
            int randomPort = portNumberGenRnd.Next(1025, 65535);
            while (!IsAvailable(randomPort, iis))
            {
                randomPort = portNumberGenRnd.Next(1025, 65535);
            }

            return randomPort;
        }

        //TODO this is duplicated in HgServer.cs, though out of sync in functionality.
        private bool IsAvailable(int port, IIS.ServerManager iis)
        {
            var tcpConnections = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections();
            foreach (var connectionInfo in tcpConnections)
            {
                if (connectionInfo.LocalEndPoint.Port == port)
                {
                    return false;
                }
            }

            foreach (var iisSite in iis.Sites)
            {
                foreach (var binding in iisSite.Bindings)
                {
                    if (binding.EndPoint != null && binding.EndPoint.Port == port)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private int CreateSite(IIS.ServerManager iis, string siteName, string siteRoot)
        {
            EnsureKuduAppPool(iis);

            int sitePort = GetRandomPort(iis);
            var site = iis.Sites.Add(siteName, siteRoot, sitePort);
            site.ApplicationDefaults.ApplicationPoolName = KuduAppPoolName;

            return sitePort;
        }

        private void DeleteSite(IIS.ServerManager iis, string siteName, bool deletePhysicalFiles = true)
        {
            var site = iis.Sites[siteName];
            if (site != null)
            {
                site.StopAndWait();
                if (deletePhysicalFiles)
                {
                    string physicalPath = site.Applications[0].VirtualDirectories[0].PhysicalPath;
                    DeleteSafe(physicalPath);
                }
                iis.Sites.Remove(site);
            }
        }

        private static string GetDevSite(string applicationName)
        {
            return "kudu_dev_" + applicationName;
        }

        private static string GetLiveSite(string applicationName)
        {
            return "kudu_" + applicationName;
        }

        private static string GetServiceSite(string applicationName)
        {
            return "kudu_service_" + applicationName;
        }

        private static void DeleteSafe(string physicalPath)
        {
            if (!Directory.Exists(physicalPath))
            {
                return;
            }

            FileSystemHelpers.DeleteDirectorySafe(physicalPath);
        }
    }
}