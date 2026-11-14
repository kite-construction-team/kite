using System.Diagnostics;
using System.Windows.Forms;
using Kite.Core.Models;
using Kite.Core.Storage;
using Kite.Desktop.WinForms;
using Kite.Management.Web;
using Kite.Server.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Web.WebView2.Core;
using Xunit;

namespace Kite.IntegrationTests;

/// <summary>
/// End-to-End (E2E) test suite driving the real Chromium engine (<see cref="CoreWebView2"/>)
/// inside <see cref="KiteMainForm"/> against the zero-port <see cref="WebView2InMemoryBridge"/>
/// and the <see cref="KiteServerEngine"/> filesystem control plane.
/// </summary>
public sealed class ChromiumDesktopE2ETests
{
    [Fact]
    public async Task Scenario1_DesktopAppResponsiveness_StartupAndNavigationUnderChromium_MustBeFast()
    {
        string tempHome = Path.Combine(Path.GetTempPath(), $"kite-chromium-perf-{Guid.NewGuid():N}");
        try
        {
            var metrics = await RunOnStaThreadAsync(async () =>
            {
                var hostStartSw = Stopwatch.StartNew();
                var layout = new KiteHomeLayout(tempHome);

                await using var inMemoryHost = new KiteInMemoryHost(layout);
                await inMemoryHost.StartAsync(seedDefaults: true);

                await using var serverEngine = new KiteServerEngine(layout, enableKestrelListeners: false);
                await serverEngine.StartAsync(seedDefaultsIfEmpty: true);
                long hostAndEngineStartupMs = hostStartSw.ElapsedMilliseconds;

                // Verify EnsureServerDaemonRunningAsync returns immediately (< 50ms) when server is online
                var daemonCheckSw = Stopwatch.StartNew();
                bool isOnline = await inMemoryHost.ManagementService.EnsureServerDaemonRunningAsync();
                long daemonCheckMs = daemonCheckSw.ElapsedMilliseconds;
                Assert.True(isOnline);

                string userDataFolder = Path.Combine(tempHome, "wv2-perf-profile");
                using var mainForm = new KiteMainForm(inMemoryHost, userDataFolder)
                {
                    ShowInTaskbar = false,
                    StartPosition = FormStartPosition.Manual,
                    Location = new System.Drawing.Point(-3000, -3000)
                };

                var initialLoadSw = Stopwatch.StartNew();
                Assert.NotNull(mainForm.Icon);
                Assert.NotEqual(System.Drawing.SystemIcons.Application.Handle, mainForm.Icon!.Handle);

                mainForm.Show();
                bool initialOk = await mainForm.ReadyTask;
                long initialLoadMs = initialLoadSw.ElapsedMilliseconds;
                Assert.True(initialOk);

                CoreWebView2 chromium = mainForm.WebViewControl.CoreWebView2;

                // 1. Click the 'Application Pools' link in the DOM (real user click on http://kite.localhost)
                var navSw = Stopwatch.StartNew();
                await ClickAndWaitForNavigationAsync(chromium, "document.querySelector('a[href=\"/?scope=pools\"]').click();");
                long poolsNavMs = navSw.ElapsedMilliseconds;

                // 2. Click the 'Sites' link in the DOM
                navSw.Restart();
                await ClickAndWaitForNavigationAsync(chromium, "document.querySelector('a[href=\"/?scope=sites\"]').click();");
                long sitesNavMs = navSw.ElapsedMilliseconds;

                // 3. Click the 'Server' link and then 'Global Server Settings' link in the DOM
                await ClickAndWaitForNavigationAsync(chromium, "document.querySelector('a[href=\"/?scope=server\"]').click();");
                navSw.Restart();
                await ClickAndWaitForNavigationAsync(chromium, "document.querySelector('a[href=\"/?scope=server&tab=settings\"]').click();");
                long settingsNavMs = navSw.ElapsedMilliseconds;

                // 4. Click back to Server Overview (warm snapshot cache)
                navSw.Restart();
                await ClickAndWaitForNavigationAsync(chromium, "document.querySelector('a[href=\"/?scope=server&tab=overview\"]').click();");
                long warmOverviewNavMs = navSw.ElapsedMilliseconds;

                // 5. Trigger Overlapped Recycle POST via real Chromium click
                await ClickAndWaitForNavigationAsync(chromium, "document.querySelector('a[href=\"/?scope=pools\"]').click();");
                navSw.Restart();
                await ClickAndWaitForNavigationAsync(chromium, "document.querySelector('button[value=\"recycle\"]').click();");
                long postActionMs = navSw.ElapsedMilliseconds;

                string bodyText = await chromium.ExecuteScriptAsync("document.body.innerText");
                mainForm.Close();

                return (hostAndEngineStartupMs, daemonCheckMs, initialLoadMs, poolsNavMs, sitesNavMs, settingsNavMs, warmOverviewNavMs, postActionMs, bodyText);
            });

            Console.WriteLine(
                $"  [CHROMIUM E2E SCENARIO 1] Host+EngineStartup={metrics.hostAndEngineStartupMs}ms | DaemonCheck={metrics.daemonCheckMs}ms | InitialWindow+WebView2={metrics.initialLoadMs}ms | PoolsClick={metrics.poolsNavMs}ms | SitesClick={metrics.sitesNavMs}ms | SettingsClick={metrics.settingsNavMs}ms | WarmOverviewClick={metrics.warmOverviewNavMs}ms | PostRecycleClick={metrics.postActionMs}ms");

            // Strict responsiveness regression gates
            Assert.True(metrics.hostAndEngineStartupMs < 350, $"Host + Engine cold startup was too slow: {metrics.hostAndEngineStartupMs}ms");
            Assert.True(metrics.daemonCheckMs < 100, $"Daemon check was too slow: {metrics.daemonCheckMs}ms");
            Assert.True(metrics.poolsNavMs < 80, $"Pools scope click was too slow: {metrics.poolsNavMs}ms");
            Assert.True(metrics.sitesNavMs < 80, $"Sites scope click was too slow: {metrics.sitesNavMs}ms");
            Assert.True(metrics.settingsNavMs < 80, $"Settings tab click was too slow: {metrics.settingsNavMs}ms");
            Assert.True(metrics.warmOverviewNavMs < 80, $"Warm overview click was too slow: {metrics.warmOverviewNavMs}ms");
            Assert.True(metrics.postActionMs < 100, $"POST form action click was too slow: {metrics.postActionMs}ms");
            Assert.Contains("RecycleVersion=2", metrics.bodyText);
        }
        finally
        {
            if (Directory.Exists(tempHome))
            {
                try { Directory.Delete(tempHome, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public async Task Scenario2_DesktopAppFullControlPlaneWorkflow_ViaChromiumDomInteractions()
    {
        string tempHome = Path.Combine(Path.GetTempPath(), $"kite-chromium-workflow-{Guid.NewGuid():N}");
        try
        {
            await RunOnStaThreadAsync(async () =>
            {
                var layout = new KiteHomeLayout(tempHome);
                await using var inMemoryHost = new KiteInMemoryHost(layout);
                await inMemoryHost.StartAsync(seedDefaults: true);

                await using var serverEngine = new KiteServerEngine(layout, enableKestrelListeners: false);
                await serverEngine.StartAsync(seedDefaultsIfEmpty: true);

                string userDataFolder = Path.Combine(tempHome, "wv2-workflow-profile");
                using var mainForm = new KiteMainForm(inMemoryHost, userDataFolder)
                {
                    ShowInTaskbar = false,
                    StartPosition = FormStartPosition.Manual,
                    Location = new System.Drawing.Point(-3000, -3000)
                };

                Assert.NotNull(mainForm.Icon);
                Assert.NotEqual(System.Drawing.SystemIcons.Application.Handle, mainForm.Icon!.Handle);

                mainForm.Show();
                Assert.True(await mainForm.ReadyTask);
                CoreWebView2 chromium = mainForm.WebViewControl.CoreWebView2;

                // Step 1: Verify Server Overview shows Online Daemon, v1 Recycle Version, and 0 Requests
                string overviewText = await chromium.ExecuteScriptAsync("document.body.innerText");
                Assert.Contains("Kite.Server Online", overviewText);
                Assert.Contains("v1", overviewText);
                Assert.DoesNotContain("v@p.Config.RecycleVersion", overviewText);

                // Step 2: Edit Global Server Settings via Chromium DOM and submit form
                await NavigateChromiumAsync(chromium, $"{WebView2InMemoryBridge.VirtualOrigin}/?scope=server&tab=settings");
                await ClickAndWaitForNavigationAsync(
                    chromium,
                    """
                    document.querySelector('input[name="serverName"]').value = 'Kite-Chromium-EdgeNode';
                    document.querySelector('input[name="maxConcurrentConnections"]').value = '4096';
                    document.querySelector('#form-save-server button[type="submit"]').click();
                    """);

                string afterServerSaveText = await chromium.ExecuteScriptAsync("document.body.innerText");
                Assert.Contains("Server configuration saved atomically", afterServerSaveText);
                Assert.Contains("Kite-Chromium-EdgeNode", afterServerSaveText);

                // Reconcile server engine and verify config applied
                await serverEngine.ConfigMonitor.ReloadIfChangedAsync();
                Assert.Equal("Kite-Chromium-EdgeNode", serverEngine.ConfigMonitor.CurrentSnapshot.Server.ServerName);
                Assert.Equal(4096, serverEngine.ConfigMonitor.CurrentSnapshot.Server.MaxConcurrentConnections);

                // Step 3: Navigate to Application Pools in Chromium, scale Web Garden to 3 workers, and save
                await NavigateChromiumAsync(chromium, $"{WebView2InMemoryBridge.VirtualOrigin}/?scope=pools&entity=default-pool");
                await ClickAndWaitForNavigationAsync(
                    chromium,
                    """
                    document.querySelector('input[name="workerCount"]').value = '3';
                    document.querySelector('select[name="gcMode"]').value = 'DatasServer';
                    document.querySelector('#form-save-pool button[type="submit"]').click();
                    """);

                await serverEngine.ConfigMonitor.ReloadIfChangedAsync();
                await serverEngine.PublishStateSnapshotsAsync();
                inMemoryHost.ManagementService.InvalidateCache();

                await NavigateChromiumAsync(chromium, $"{WebView2InMemoryBridge.VirtualOrigin}/?scope=pools&entity=default-pool");
                string poolsText = await chromium.ExecuteScriptAsync("document.body.innerText");
                Assert.Contains("w3", poolsText);
                Assert.Contains("w4", poolsText);
                Assert.Contains("w5", poolsText);

                // Step 4: Serve a real request through KiteServerEngine pipeline, flush W3C logs, and inspect in Chromium UI
                var reqCtx = new DefaultHttpContext();
                reqCtx.Request.Method = "GET";
                reqCtx.Request.Scheme = "http";
                reqCtx.Request.Headers["Host"] = "localhost";
                reqCtx.Connection.LocalPort = 80;
                reqCtx.Request.Path = "/index.html";
                reqCtx.Response.Body = new MemoryStream();
                await serverEngine.Pipeline.ExecuteAsync(reqCtx);
                Assert.Equal(200, reqCtx.Response.StatusCode);

                await serverEngine.PublishStateSnapshotsAsync();
                inMemoryHost.ManagementService.InvalidateCache();

                await NavigateChromiumAsync(chromium, $"{WebView2InMemoryBridge.VirtualOrigin}/?scope=sites&entity=default-site&tab=logs");
                string logsText = await chromium.ExecuteScriptAsync("document.body.innerText");
                Assert.Contains("/index.html", logsText);
                Assert.Contains("200", logsText);

                // Step 5: Verify Obsidian Telemetry 4-View Sidebar Navigation & Design Tokens
                string bgColor = await chromium.ExecuteScriptAsync("getComputedStyle(document.body).backgroundColor");
                Assert.Contains("rgb(14, 14, 18)", bgColor); // #0e0e12 surface-container-lowest

                await ClickAndWaitForNavigationAsync(chromium, "document.querySelector('a[data-path=\"configuration-and-features\"]').click();");
                string configMatrixText = await chromium.ExecuteScriptAsync("document.body.innerText");
                Assert.Contains("HTTP & ROUTING ENGINE", configMatrixText.ToUpperInvariant());
                Assert.Contains("config/server.json Live Inspector", configMatrixText);

                await ClickAndWaitForNavigationAsync(chromium, "document.querySelector('a[data-path=\"worker-processes-and-tracing\"]').click();");
                string workersText = await chromium.ExecuteScriptAsync("document.body.innerText");
                Assert.Contains("Active Worker Processes", workersText);
                Assert.Contains("Isolated Execution Engines", workersText);

                mainForm.Close();
                return true;
            });
        }
        finally
        {
            if (Directory.Exists(tempHome))
            {
                try { Directory.Delete(tempHome, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public async Task Scenario3_Milestone2FeatureMatrix_FrebTracing_AndWorkerDiagnostics_ViaChromium()
    {
        string tempHome = Path.Combine(Path.GetTempPath(), $"kite-chromium-m2-{Guid.NewGuid():N}");
        try
        {
            await RunOnStaThreadAsync(async () =>
            {
                var layout = new KiteHomeLayout(tempHome);
                await using var inMemoryHost = new KiteInMemoryHost(layout);
                await inMemoryHost.StartAsync(seedDefaults: true);

                await using var serverEngine = new KiteServerEngine(layout, enableKestrelListeners: false);
                await serverEngine.StartAsync(seedDefaultsIfEmpty: true);

                string userDataFolder = Path.Combine(tempHome, "wv2-m2-profile");
                using var mainForm = new KiteMainForm(inMemoryHost, userDataFolder)
                {
                    ShowInTaskbar = false,
                    StartPosition = FormStartPosition.Manual,
                    Location = new System.Drawing.Point(-3000, -3000)
                };

                Assert.NotNull(mainForm.Icon);
                Assert.NotEqual(System.Drawing.SystemIcons.Application.Handle, mainForm.Icon!.Handle);

                mainForm.Show();
                Assert.True(await mainForm.ReadyTask);
                CoreWebView2 chromium = mainForm.WebViewControl.CoreWebView2;

                // Step 1: Open Configuration & Features Matrix -> Configure URL Rewrite via Modal
                await ClickAndWaitForNavigationAsync(chromium, "document.querySelector('a[data-path=\"configuration-and-features\"]').click();");
                await ClickAndWaitForNavigationAsync(
                    chromium,
                    """
                    toggleModal('modal-rewrite', true);
                    document.querySelector('#form-feature-rewrite input[name="ruleName"]').value = 'OldToNew301';
                    document.querySelector('#form-feature-rewrite select[name="ruleActionType"]').value = 'Redirect301';
                    document.querySelector('#form-feature-rewrite input[name="rulePattern"]').value = '^/old/(.*)$';
                    document.querySelector('#form-feature-rewrite input[name="ruleTarget"]').value = '/new/{R:1}';
                    document.querySelector('#form-feature-rewrite button[name="replaceExisting"]').click();
                    """);
                string rewriteAck = await chromium.ExecuteScriptAsync("document.body.innerText");
                Assert.Contains("Updated URL Rewrite rules", rewriteAck);

                // Step 2: Configure IP Subnet CIDR Gate (allow 127.0.0.1/32, deny unmatched 403.6) via Modal
                await ClickAndWaitForNavigationAsync(
                    chromium,
                    """
                    toggleModal('modal-ip', true);
                    document.querySelector('#form-feature-ip select[name="ipEnabled"]').value = 'true';
                    document.querySelector('#form-feature-ip select[name="allowUnmatched"]').value = 'false';
                    document.querySelector('#form-feature-ip input[name="cidr"]').value = '127.0.0.1/32';
                    document.querySelector('#form-feature-ip select[name="ruleAllow"]').value = 'true';
                    document.querySelector('#form-feature-ip button.cyber-btn-solid').click();
                    """);
                string ipAck = await chromium.ExecuteScriptAsync("document.body.innerText");
                Assert.Contains("Updated IP & Subnet Filtering rules", ipAck);

                // Step 3: Configure FREB to capture 403 & 500-599 traces via Modal + verify Visual Tree / Raw JSON toggle
                await ClickAndWaitForNavigationAsync(
                    chromium,
                    """
                    toggleModal('modal-freb', true);
                    document.querySelector('#form-feature-freb input[name="statusCodeTrigger"]').value = '403, 500-599';
                    document.querySelector('#form-feature-freb button.cyber-btn-solid').click();
                    """);
                await chromium.ExecuteScriptAsync("switchInspectorTab('visual');");
                string visualHidden = await chromium.ExecuteScriptAsync("document.getElementById('visual-tree-container').classList.contains('hidden')");
                Assert.Equal("false", visualHidden);

                // Step 4: Hot-reload KiteServerEngine and verify Rewrite 301, IP Gate 403.6, and FREB capture
                await serverEngine.ConfigMonitor.ReloadIfChangedAsync();

                var blockedReq = new DefaultHttpContext();
                blockedReq.Request.Method = "GET";
                blockedReq.Request.Scheme = "http";
                blockedReq.Request.Headers["Host"] = "localhost";
                blockedReq.Connection.LocalPort = 80;
                blockedReq.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("198.51.100.10");
                blockedReq.Request.Path = "/index.html";
                blockedReq.Response.Body = new MemoryStream();
                await serverEngine.Pipeline.ExecuteAsync(blockedReq);
                Assert.Equal(403, blockedReq.Response.StatusCode);
                Assert.Equal("403.6", blockedReq.Response.Headers["X-Kite-SubStatus"].ToString());

                var redirectReq = new DefaultHttpContext();
                redirectReq.Request.Method = "GET";
                redirectReq.Request.Scheme = "http";
                redirectReq.Request.Headers["Host"] = "localhost";
                redirectReq.Connection.LocalPort = 80;
                redirectReq.Connection.RemoteIpAddress = System.Net.IPAddress.Loopback;
                redirectReq.Request.Path = "/old/docs/readme.html";
                redirectReq.Response.Body = new MemoryStream();
                await serverEngine.Pipeline.ExecuteAsync(redirectReq);
                Assert.Equal(301, redirectReq.Response.StatusCode);
                Assert.Equal("/new/docs/readme.html", redirectReq.Response.Headers["Location"].ToString());

                await serverEngine.PublishStateSnapshotsAsync();
                inMemoryHost.ManagementService.InvalidateCache();

                // Step 5: Navigate to Worker Processes & Tracing -> Open Thread Modal & FREB Slide-Over Inspector Drawer
                await ClickAndWaitForNavigationAsync(chromium, "document.querySelector('a[data-path=\"worker-processes-and-tracing\"]').click();");
                string workersText = await chromium.ExecuteScriptAsync("document.body.innerText");
                Assert.Contains("403 FREB", workersText);

                await chromium.ExecuteScriptAsync("document.querySelector('button[title=\"Inspect Threads\"]').click();");
                string threadModalVisible = await chromium.ExecuteScriptAsync("!document.getElementById('thread-modal').classList.contains('hidden')");
                Assert.Equal("true", threadModalVisible);
                await chromium.ExecuteScriptAsync("toggleModal('thread-modal', false);");

                await chromium.ExecuteScriptAsync("document.querySelector('.request-item').click();");
                string drawerVisible = await chromium.ExecuteScriptAsync("!document.getElementById('inspector-panel').classList.contains('hidden')");
                string drawerText = await chromium.ExecuteScriptAsync("document.getElementById('inspector-panel').innerText");
                Assert.Equal("true", drawerVisible);
                Assert.Contains("RequestFilteringAndIpGate", drawerText);
                Assert.Contains("198.51.100.10", drawerText);

                // Step 6: Return to Dashboard & Telemetry and verify live telemetry buckets reflected
                await ClickAndWaitForNavigationAsync(chromium, "document.querySelector('a[data-path=\"dashboard-and-telemetry\"]').click();");
                string dashText = await chromium.ExecuteScriptAsync("document.body.innerText");
                Assert.Contains("Mbps", dashText);

                mainForm.Close();
                return true;
            });
        }
        finally
        {
            if (Directory.Exists(tempHome))
            {
                try { Directory.Delete(tempHome, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public async Task Scenario4_Milestone3EdgeModulesSseAndModals_WorkUnderChromium()
    {
        string tempHome = Path.Combine(Path.GetTempPath(), $"kite-chromium-m3-{Guid.NewGuid():N}");
        try
        {
            var layout = new KiteHomeLayout(tempHome);
            await using var inMemoryHost = new KiteInMemoryHost(layout);
            await inMemoryHost.StartAsync(seedDefaults: true);

            await using var serverEngine = new KiteServerEngine(layout, enableKestrelListeners: false);
            await serverEngine.StartAsync(seedDefaultsIfEmpty: true);

            // Verify SSE live stream response returns valid event frame directly
            using InMemoryUiResponse sseResp = await inMemoryHost.SendAsync("/api/events/stream");
            Assert.Equal(200, sseResp.StatusCode);
            Assert.StartsWith("text/event-stream", sseResp.ContentType);
            string sseBody = await sseResp.ReadBodyAsStringAsync();
            Assert.Contains("event: telemetry", sseBody);

            await RunOnStaThreadAsync(async () =>
            {
                string userDataFolder = Path.Combine(tempHome, "wv2-m3-profile");
                using var mainForm = new KiteMainForm(inMemoryHost, userDataFolder)
                {
                    ShowInTaskbar = false,
                    StartPosition = FormStartPosition.Manual,
                    Location = new System.Drawing.Point(-3000, -3000)
                };

                Assert.NotNull(mainForm.Icon);
                Assert.NotEqual(System.Drawing.SystemIcons.Application.Handle, mainForm.Icon!.Handle);

                mainForm.Show();
                Assert.True(await mainForm.ReadyTask);
                CoreWebView2 chromium = mainForm.WebViewControl.CoreWebView2;

                // Navigate to Configuration & Edge Modules (16 Modules)
                await ClickAndWaitForNavigationAsync(chromium, "document.querySelector('a[data-path=\"configuration-and-features\"]').click();");
                string pageText = (await chromium.ExecuteScriptAsync("document.body.innerText")).ToUpperInvariant();
                Assert.Contains("16 MODULES", pageText);
                Assert.Contains("EDGE RATE LIMITING", pageText);
                Assert.Contains("UPSTREAM PROXY CLUSTERS", pageText);
                Assert.Contains("SPA FALLBACK & EDGE WAF", pageText);

                // Open Rate Limiting modal and save via real Chromium submit button click
                await chromium.ExecuteScriptAsync("toggleModal('modal-ratelimit', true);");
                await ClickAndWaitForNavigationAsync(chromium, @"
                    const f = document.getElementById('form-feature-ratelimit');
                    f.querySelector('input[name=rateLimitEnabled]').checked = true;
                    f.querySelector('input[name=permitLimit]').value = '250';
                    f.querySelector('button[type=""submit""]').click();
                ");

                string updatedText = (await chromium.ExecuteScriptAsync("document.body.innerText")).ToUpperInvariant();
                Assert.Contains("250 REQ", updatedText);

                // Also add an Upstream Cluster via Modal 15
                await chromium.ExecuteScriptAsync("toggleModal('modal-upstreams', true);");
                await ClickAndWaitForNavigationAsync(chromium, @"
                    const uf = document.getElementById('form-feature-upstreams');
                    uf.querySelector('input[name=clusterId]').value = 'canary-cluster';
                    uf.querySelector('input[name=destinationAddresses]').value = 'http://127.0.0.1:5099';
                    uf.querySelector('button[type=""submit""]:not([name=removeCluster])').click();
                ");

                // Navigate to Sites & Pools and edit Site Routing Inspector (#form-save-site)
                await ClickAndWaitForNavigationAsync(chromium, "document.querySelector('a[href=\"/?scope=sites\"]').click();");
                string siteFormsCount = await chromium.ExecuteScriptAsync("document.querySelectorAll('#form-save-site').length");
                Assert.Equal("1", siteFormsCount);
                string sitesText = (await chromium.ExecuteScriptAsync("document.body.innerText")).ToUpperInvariant();
                Assert.Contains("PRIMARY UPSTREAM CLUSTER", sitesText);
                Assert.Contains("CANARY TRAFFIC SPLIT WEIGHT", sitesText);

                await ClickAndWaitForNavigationAsync(chromium, @"
                    const sf = document.getElementById('form-save-site');
                    sf.querySelector('select[name=hostingMode]').value = 'ExternalReverseProxy';
                    sf.querySelector('select[name=upstreamClusterId]').value = 'canary-cluster';
                    sf.querySelector('input[name=canaryWeightPercent]').value = '15';
                    sf.querySelector('input[name=enableStreamingPassThrough]').checked = true;
                    document.querySelector('button[form=""form-save-site""]').click();
                ");

                string savedSitesText = (await chromium.ExecuteScriptAsync("document.body.innerText")).ToUpperInvariant();
                Assert.Contains("EXTERNALREVERSEPROXY", savedSitesText);

                // Navigate to Dashboard and verify Upstream Reverse Proxy Clusters & Modern Edge Telemetry card
                await ClickAndWaitForNavigationAsync(chromium, "document.querySelector('a[data-path=\"dashboard-and-telemetry\"]').click();");
                string dashText = (await chromium.ExecuteScriptAsync("document.body.innerText")).ToUpperInvariant();
                Assert.Contains("UPSTREAM REVERSE PROXY CLUSTERS", dashText);
                Assert.Contains("CANARY-CLUSTER", dashText);
                Assert.Contains("MODERN EDGE TELEMETRY", dashText);

                string duplicateWindowCtrls = await chromium.ExecuteScriptAsync("document.querySelectorAll('button[aria-label=\"Minimize\"],button[aria-label=\"Maximize\"],button[aria-label=\"Close\"]').length");
                Assert.Equal("0", duplicateWindowCtrls);

                mainForm.Close();
                return true;
            });
        }
        finally
        {
            if (Directory.Exists(tempHome))
            {
                try { Directory.Delete(tempHome, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public async Task Scenario4_Milestone4UiWorkflows_RoutesAutoscaleDebuggerImportAndHistory_WorkEndToEnd()
    {
        string tempHome = Path.Combine(Path.GetTempPath(), $"kite-chromium-m4-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempHome);
        try
        {
            await RunOnStaThreadAsync(async () =>
            {
                var layout = new KiteHomeLayout(tempHome);
                await using var inMemoryHost = new KiteInMemoryHost(layout);
                await inMemoryHost.StartAsync(seedDefaults: true);

                await using var serverEngine = new KiteServerEngine(layout, enableKestrelListeners: false);
                await serverEngine.StartAsync(seedDefaultsIfEmpty: true);

                string userDataFolder = Path.Combine(tempHome, "wv2-m4-profile");
                using var mainForm = new KiteMainForm(inMemoryHost, userDataFolder)
                {
                    ShowInTaskbar = false,
                    StartPosition = FormStartPosition.Manual,
                    Location = new System.Drawing.Point(-3000, -3000)
                };

                mainForm.Show();
                Assert.True(await mainForm.ReadyTask);
                CoreWebView2 chromium = mainForm.WebViewControl.CoreWebView2;

                // 1. Sites view: verify Blue/Green slot card & add declarative Route Rule via #form-site-route-add
                await NavigateChromiumAsync(chromium, $"{WebView2InMemoryBridge.VirtualOrigin}/?scope=sites&entity=default-site");
                string routeFormExists = await chromium.ExecuteScriptAsync("Boolean(document.querySelector('#form-site-route-add'))");
                Assert.Equal("true", routeFormExists);
                string slotFormExists = await chromium.ExecuteScriptAsync("Boolean(document.querySelector('#form-site-slot-deploy'))");
                Assert.Equal("true", slotFormExists);

                await ClickAndWaitForNavigationAsync(
                    chromium,
                    """
                    document.querySelector('#form-site-route-add input[name="routeId"]').value = 'e2e-route';
                    document.querySelector('#form-site-route-add input[name="pathPrefix"]').value = '/api/v1';
                    document.querySelector('#form-site-route-add button[type="submit"]').click();
                    """);
                string siteBody = await chromium.ExecuteScriptAsync("document.body.innerText");
                Assert.Contains("e2e-route", siteBody);

                // 2. Pools view: verify MinWorkers/MaxWorkers autoscale inputs on #form-save-pool and save
                await NavigateChromiumAsync(chromium, $"{WebView2InMemoryBridge.VirtualOrigin}/?scope=pools&entity=default-pool");
                await ClickAndWaitForNavigationAsync(
                    chromium,
                    """
                    document.querySelector('#form-save-pool input[name="minWorkers"]').value = '0';
                    document.querySelector('#form-save-pool input[name="maxWorkers"]').value = '4';
                    document.querySelector('#form-save-pool button[type="submit"]').click();
                    """);

                (ApplicationPoolConfig? savedPool, _) = await Kite.Core.Storage.AtomicJsonStore.ReadWithHashAsync(
                    layout.GetPoolConfigPath("default-pool"),
                    Kite.Core.Serialization.KiteJsonContext.Default.ApplicationPoolConfig);
                Assert.Equal(0, savedPool?.ProcessModel.MinWorkers);
                Assert.Equal(4, savedPool?.ProcessModel.MaxWorkers);

                // 3. Config Matrix: verify L4 Streams card, open Pipeline Debugger & run dry-run simulation
                var navSw = Stopwatch.StartNew();
                await ClickAndWaitForNavigationAsync(chromium, "document.querySelector('a[data-path=\"configuration-and-features\"]').click();");
                Assert.True(navSw.ElapsedMilliseconds < 250, $"Navigation took {navSw.ElapsedMilliseconds}ms");

                string cfgBody = await chromium.ExecuteScriptAsync("document.body.innerText");
                Assert.Contains("L4 TCP & TLS SNI Streams", cfgBody);
                Assert.Equal("true", await chromium.ExecuteScriptAsync("Boolean(document.querySelector('#modal-import'))"));
                Assert.Equal("true", await chromium.ExecuteScriptAsync("Boolean(document.querySelector('#modal-history'))"));

                await chromium.ExecuteScriptAsync("""
                    document.getElementById('btn-open-debugger').click();
                    document.getElementById('dbg-path').value = '/api/v1/orders';
                    document.getElementById('btn-run-debugger').click();
                    """);

                string simText = "";
                for (int i = 0; i < 30; i++)
                {
                    await Task.Delay(50);
                    simText = await chromium.ExecuteScriptAsync("document.getElementById('dbg-results').innerText");
                    if (simText.Contains("e2e-route", StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }
                }
                Assert.Contains("e2e-route", simText);

                // 4. Open History modal and verify snapshots from previous saves loaded via /api/history
                await chromium.ExecuteScriptAsync("document.getElementById('btn-open-history').click();");
                string histText = "";
                for (int i = 0; i < 30; i++)
                {
                    await Task.Delay(50);
                    histText = await chromium.ExecuteScriptAsync("document.getElementById('history-list').innerText");
                    if (histText.Contains("Restore", StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }
                }
                Assert.Contains("Restore", histText);

                mainForm.Close();
                return true;
            });
        }
        finally
        {
            if (Directory.Exists(tempHome))
            {
                try { Directory.Delete(tempHome, recursive: true); } catch { }
            }
        }
    }

    private static async Task<T> RunOnStaThreadAsync<T>(Func<Task<T>> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var staThread = new Thread(() =>
        {
            try
            {
                Application.SetHighDpiMode(HighDpiMode.SystemAware);
                Application.EnableVisualStyles();
                var syncCtx = new WindowsFormsSynchronizationContext();
                SynchronizationContext.SetSynchronizationContext(syncCtx);

                _ = ExecuteAsync();
                Application.Run();

                async Task ExecuteAsync()
                {
                    try
                    {
                        T result = await work();
                        tcs.TrySetResult(result);
                    }
                    catch (Exception ex)
                    {
                        tcs.TrySetException(ex);
                    }
                    finally
                    {
                        Application.ExitThread();
                    }
                }
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });

        staThread.SetApartmentState(ApartmentState.STA);
        staThread.IsBackground = true;
        staThread.Start();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using var reg = cts.Token.Register(() => tcs.TrySetCanceled());
        return await tcs.Task;
    }

    private static async Task NavigateChromiumAsync(CoreWebView2 chromium, string url)
    {
        var navTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? sender, CoreWebView2NavigationCompletedEventArgs e) => navTcs.TrySetResult(e.IsSuccess);

        chromium.NavigationCompleted += Handler;
        try
        {
            chromium.Navigate(url);
            bool ok = await navTcs.Task;
            if (!ok)
            {
                throw new InvalidOperationException($"Chromium navigation to '{url}' failed.");
            }
        }
        finally
        {
            chromium.NavigationCompleted -= Handler;
        }
    }

    private static async Task ClickAndWaitForNavigationAsync(CoreWebView2 chromium, string jsScript)
    {
        var navTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? sender, CoreWebView2NavigationCompletedEventArgs e) => navTcs.TrySetResult(e.IsSuccess);

        chromium.NavigationCompleted += Handler;
        try
        {
            await chromium.ExecuteScriptAsync(jsScript);
            bool ok = await navTcs.Task;
            if (!ok)
            {
                throw new InvalidOperationException("Chromium form navigation failed.");
            }
        }
        finally
        {
            chromium.NavigationCompleted -= Handler;
        }
    }
}
