// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

#if DNX451
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml;
using Microsoft.Framework.Logging;
using Microsoft.Web.Administration;

namespace Microsoft.AspNet.Server.Testing
{
    /// <summary>
    /// Deployer for IIS.
    /// </summary>
    public class IISDeployer : ApplicationDeployer
    {
        private IISApplication _application;
        private CancellationTokenSource _hostShutdownToken = new CancellationTokenSource();
        private static object _syncObject = new object();

        public IISDeployer(DeploymentParameters startParameters, ILogger logger)
            : base(startParameters, logger)
        {
        }

        public override DeploymentResult Deploy()
        {
            // Start timer
            StartTimer();

            // Only supports publish and run on IIS.
            DeploymentParameters.PublishApplicationBeforeDeployment = true;

            _application = new IISApplication(DeploymentParameters, Logger);

            DeploymentParameters.DnxRuntime = PopulateChosenRuntimeInformation();

            // Publish to IIS root\application folder.
            DnuPublish(publishRoot: _application.WebSiteRootFolder);

            // Drop an ini file instead of setting environment variable.
            SetAspEnvironmentWithIni();

            // Setup the IIS Application.
            if (DeploymentParameters.ServerType == ServerType.IISNativeModule)
            {
                TurnRammFarOnNativeModule();
            }

            lock (_syncObject)
            {
                // To prevent modifying the IIS setup concurrently.
                _application.Deploy();
            }

            // Warm up time for IIS setup.
            Thread.Sleep(1 * 1000);
            Logger.LogInformation("Successfully finished IIS application directory setup.");

            return new DeploymentResult
            {
                WebRootLocation = DeploymentParameters.ApplicationPath,
                DeploymentParameters = DeploymentParameters,
                // Accomodate the vdir name.
                ApplicationBaseUri = new UriBuilder(Uri.UriSchemeHttp, "localhost", IISApplication.Port, _application.VirtualDirectoryName).Uri.AbsoluteUri + "/",
                HostShutdownToken = _hostShutdownToken.Token
            };
        }

        private void SetAspEnvironmentWithIni()
        {
            // Drop a Microsoft.AspNet.Hosting.ini with ASPNET_ENV information.
            Logger.LogInformation("Creating Microsoft.AspNet.Hosting.ini file with ASPNET_ENV.");
            var iniFile = Path.Combine(DeploymentParameters.ApplicationPath, "Microsoft.AspNet.Hosting.ini");
            File.WriteAllText(iniFile, string.Format("ASPNET_ENV={0}", DeploymentParameters.EnvironmentName));
        }

        private void TurnRammFarOnNativeModule()
        {
            Logger.LogInformation("Turning runAllManagedModulesForAllRequests=true in web.config for native module.");
            var webConfig = Path.Combine(DeploymentParameters.ApplicationPath, "web.config");
            var configuration = new XmlDocument();
            configuration.LoadXml(File.ReadAllText(webConfig));

            // https://github.com/aspnet/Helios/issues/77
            var rammfarAttribute = configuration.CreateAttribute("runAllManagedModulesForAllRequests");
            rammfarAttribute.Value = "true";
            var modulesNode = configuration.CreateElement("modules");
            modulesNode.Attributes.Append(rammfarAttribute);
            var systemWebServerNode = configuration.CreateElement("system.webServer");
            systemWebServerNode.AppendChild(modulesNode);
            configuration.SelectSingleNode("//configuration").AppendChild(systemWebServerNode);
            configuration.Save(webConfig);
        }

        public override void Dispose()
        {
            if (_application != null)
            {
                lock (_syncObject)
                {
                    // Sequentialize IIS operations.
                    _application.StopAndDeleteAppPool();
                }

                TriggerHostShutdown(_hostShutdownToken);
            }

            CleanPublishedOutput();
            InvokeUserApplicationCleanup();

            StopTimer();
        }

        private class IISApplication
        {
            private const string WebSiteName = "ASPNETTESTRUNS";
            private const string NativeModuleManagedRuntimeVersion = "vCoreFX";
            private const string Dotnet45RuntimeVersion = "v4.0.30319";

            private readonly ServerManager _serverManager = new ServerManager();
            private readonly DeploymentParameters _deploymentParameters;
            private readonly ILogger _logger;
            private ApplicationPool _applicationPool;
            private Application _application;
            private Site _website;

            public string VirtualDirectoryName { get; set; }

            // Always create website with the same port.
            public const int Port = 5100;

            public string WebSiteRootFolder
            {
                get
                {
                    return Path.Combine(
                        Environment.GetEnvironmentVariable("SystemDrive") + @"\",
                        "inetpub",
                        WebSiteName);
                }
            }

            public IISApplication(DeploymentParameters deploymentParameters, ILogger logger)
            {
                _deploymentParameters = deploymentParameters;
                _logger = logger;
            }

            public void Deploy()
            {
                VirtualDirectoryName = new DirectoryInfo(_deploymentParameters.ApplicationPath).Parent.Name;
                _applicationPool = CreateAppPool(VirtualDirectoryName);
                _application = Website.Applications.Add("/" + VirtualDirectoryName, _deploymentParameters.ApplicationPath);
                _application.ApplicationPoolName = _applicationPool.Name;
                _serverManager.CommitChanges();
            }

            private Site Website
            {
                get
                {
                    _website = _serverManager.Sites.Where(s => s.Name == WebSiteName).FirstOrDefault();
                    if (_website == null)
                    {
                        _website = _serverManager.Sites.Add(WebSiteName, WebSiteRootFolder, Port);
                    }

                    return _website;
                }
            }

            private ApplicationPool CreateAppPool(string appPoolName)
            {
                var applicationPool = _serverManager.ApplicationPools.Add(appPoolName);
                applicationPool.ManagedRuntimeVersion =
                    _deploymentParameters.ServerType == ServerType.IISNativeModule ?
                    NativeModuleManagedRuntimeVersion :
                    Dotnet45RuntimeVersion;

                applicationPool.Enable32BitAppOnWin64 = (_deploymentParameters.RuntimeArchitecture == RuntimeArchitecture.x86);
                _logger.LogInformation("Created {bit} application pool '{name}' with runtime version {runtime}.",
                    _deploymentParameters.RuntimeArchitecture, applicationPool.Name,
                    string.IsNullOrEmpty(applicationPool.ManagedRuntimeVersion) ? "that is default" : applicationPool.ManagedRuntimeVersion);
                return applicationPool;
            }

            public void StopAndDeleteAppPool()
            {
                _logger.LogInformation("Stopping application pool '{name}' and deleting application.", _applicationPool.Name);

                if (_applicationPool != null)
                {
                    _applicationPool.Stop();
                }

                // Remove the application from website.
                if (_application != null)
                {
                    _application = Website.Applications.Where(a => a.Path == _application.Path).FirstOrDefault();
                    Website.Applications.Remove(_application);
                    _serverManager.ApplicationPools.Remove(_serverManager.ApplicationPools[_applicationPool.Name]);
                    _serverManager.CommitChanges();
                    _logger.LogInformation("Successfully stopped application pool '{name}' and deleted application from IIS.", _applicationPool.Name);
                }
            }
        }
    }
}
#endif