﻿using Meadow.Contract;
using Meadow.Core.Utils;
using Meadow.CoverageReport;
using Meadow.CoverageReport.Debugging;
using Meadow.JsonRpc;
using Meadow.JsonRpc.Client;
using Meadow.JsonRpc.Types;
using Meadow.JsonRpc.Types.Debugging;
using Meadow.TestNode;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Configuration;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

[assembly: Parallelize(Workers = 0, Scope = ExecutionScope.MethodLevel)]

namespace Meadow.UnitTestTemplate
{

    public static class Global
    {
        public static readonly AsyncObjectPool<TestServices> TestServicesPool;
        public static readonly ConcurrentBag<(CompoundCoverageMap Coverage, SolcBytecodeInfo Contract)[]> CoverageMaps;
        public static readonly ConcurrentBag<UnitTestResult> UnitTestResults;

        private static string _callerFilePath;

        const string REPORT_DIR = "Report";


        /// <summary>
        /// Initially populated by app.config (if set).
        /// Defaults to 5 000 000 if not set.
        /// </summary>
        [DefaultValue(5_000_000), DisplayName("DefaultGasLimit")]
        [System.ComponentModel.Description("TODO")]
        public static long? DefaultGasLimit { get; set; }

        /// <summary>
        /// Initially populated by app.config (if set).
        /// Defaults to 100 000 000 000 if not set.
        /// </summary>
        [DefaultValue(100_000_000_000), DisplayName("DefaultGasPrice")]
        [System.ComponentModel.Description("TODO")]
        public static long? DefaultGasPrice { get; set; }

        /// <summary>
        /// Initially populated by app.config (if set).
        /// Defaults to 100 if not set;
        /// </summary>
        [DefaultValue(100), DisplayName("AccountCount")]
        [System.ComponentModel.Description("TODO")]
        public static int? AccountCount { get; set; }

        /// <summary>
        /// Initially populated by app.config (if set).
        /// Defaults to 2000 if not set.
        /// </summary>
        [DefaultValue(2000), DisplayName("AccountBalance")]
        [System.ComponentModel.Description("TODO")]
        public static long? AccountBalance { get; set; }

        /// <summary>
        /// Initially populated by app.config (if set). Describes the endpoint of an external node 
        /// to connect to and tests against.
        /// </summary>
        [DefaultValue(null), DisplayName("ExternalNodeHost")]
        [System.ComponentModel.Description("Describes the endpoint of an external node")]
        internal static Uri ExternalNodeHost { get; set; }

        /// <summary>
        /// Initially populated by app.config (if set). Indicates if the external node should be used in place
        /// of tests intended for the built-in node. Enabling this option disables the built-in node, and parallel
        /// tests will be run singularly only against the external node.
        /// </summary>
        [DefaultValue(false), DisplayName("OnlyUseExternalNode")]
        [System.ComponentModel.Description("TODO")]
        public static bool? OnlyUseExternalNode { get; set; }

        [DefaultValue(null), DisplayName("SolcVersion")]
        [System.ComponentModel.Description("Currently only used in MSBuild script")]
        static string SolcVersion { get; set; }

        [DefaultValue(0), DisplayName("SolcOptimizer")]
        [System.ComponentModel.Description("Currently only used in MSBuild script")]
        static int? SolcOptimizer { get; set; }
        

        /// <summary>
        /// Identifies information for tests to be run on an external node.
        /// </summary>
        public static TestServices ExternalNodeTestServices { get; private set; }

        public static bool IsInitialized { get; private set; } = false;

        //static readonly Dictionary<string, string[]> AppConfigValues = new Dictionary<string, string[]>();
        static (string Key, string Value)[] appConfigValues;


        static Global()
        {
            // Initialize our test variables.
            TestServicesPool = new AsyncObjectPool<TestServices>(CreateTestServicesInstance);
            CoverageMaps = new ConcurrentBag<(CompoundCoverageMap Coverage, SolcBytecodeInfo Contract)[]>();
            UnitTestResults = new ConcurrentBag<UnitTestResult>();
        }

        static void ParseAppConfigSettings(Assembly callingAssembly)
        {
            var assemblyConfig = ConfigurationManager.OpenExeConfiguration(callingAssembly.Location);

            appConfigValues = assemblyConfig.AppSettings
                .Settings
                .Cast<KeyValueConfigurationElement>()
                .Select(s => (s.Key, s.Value))
                .ToArray();

            var configValueList = appConfigValues.ToList();

            SetupConfigValue(
                configValueList, 
                () => OnlyUseExternalNode, 
                str => bool.Parse(str));

            SetupConfigValue(
                configValueList,
                () => ExternalNodeHost,
                str => ServerEndpointParser.Parse(str));

            SetupConfigValue(
                configValueList, 
                () => DefaultGasLimit, 
                str => long.Parse(str, NumberStyles.AllowThousands | NumberStyles.Integer, CultureInfo.InvariantCulture));

            SetupConfigValue(
                configValueList,
                () => DefaultGasPrice,
                str => long.Parse(str, NumberStyles.AllowThousands | NumberStyles.Integer, CultureInfo.InvariantCulture));

            SetupConfigValue(
                configValueList,
                () => AccountCount,
                str => int.Parse(str, NumberStyles.AllowThousands | NumberStyles.Integer, CultureInfo.InvariantCulture));

            SetupConfigValue(
                configValueList,
                () => AccountBalance,
                str => int.Parse(str, NumberStyles.AllowThousands, CultureInfo.InvariantCulture));

            SetupConfigValue(
                configValueList,
                () => SolcVersion,
                str => str);

            SetupConfigValue(
                configValueList,
                () => SolcOptimizer,
                str => int.Parse(str, NumberStyles.AllowThousands | NumberStyles.Integer, CultureInfo.InvariantCulture));

            var unknownConfigOptions = new List<Exception>();
            foreach (var opts in configValueList)
            {
                unknownConfigOptions.Add(new Exception($"Unknown setting in app.config '{opts.Key}={opts.Value}'"));
            }

            if (unknownConfigOptions.Count == 1)
            {
                throw unknownConfigOptions[0];
            }
            else if (unknownConfigOptions.Count > 1)
            {
                throw new AggregateException("Multiple unknown settings in app.config", unknownConfigOptions.ToArray());
            }

            if (ExternalNodeHost != null)
            {
                CreateExternalNodeClient();
            }

        }

        static void SetupConfigValue<TVal>(
            List<(string Key, string Value)> configEntries,
            Expression<Func<TVal?>> exp,
            Func<string, TVal> configParser) where TVal : struct
        {
            var displayName = AttributeHelper.GetAttribute<DisplayNameAttribute>(exp).DisplayName;
            SetupConfigValue(
                configEntries,
                displayName,
                configParser,
                () => AttributeHelper.GetDefault(exp),
                v => ExpressionUtil.GetSetter(exp)(v));
        }

        static void SetupConfigValue<TVal>(
            List<(string Key, string Value)> configEntries,
            Expression<Func<TVal>> exp, 
            Func<string, TVal> configParser)
        {
            var displayName = AttributeHelper.GetAttribute<DisplayNameAttribute>(exp).DisplayName;
            SetupConfigValue(
                configEntries,
                displayName,
                configParser,
                () => AttributeHelper.GetDefault(exp),
                v => ExpressionUtil.GetSetter(exp)(v));
        }

        static void SetupConfigValue<TVal>(
            List<(string Key, string Value)> configEntries,
            string displayName,
            Func<string, TVal> configParser,
            Func<TVal> getDefaultValue,
            Action<TVal> setValue)
        {
            (string Key, string Value) configEntry = default;

            for (var i = 0; i < configEntries.Count; i++)
            {
                if (configEntries[i].Key.Equals(displayName, StringComparison.OrdinalIgnoreCase))
                {
                    configEntry = configEntries[i];
                    configEntries.RemoveAt(i);
                    break;
                }
            }

            TVal resultValue;

            if (!string.IsNullOrWhiteSpace(configEntry.Value))
            {
                try
                {
                    resultValue = configParser(configEntry.Value);
                }
                catch (Exception ex)
                {
                    throw new Exception($"Could not parse app.config '{displayName}' value '{configEntry.Value}'", ex);
                }
            }
            else
            {
                resultValue = getDefaultValue();
            }

            setValue(resultValue);
        }

        static bool TryGetAppConfigValue(string key, out string value)
        {
            var match = appConfigValues.FirstOrDefault(c => c.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (match.Value != null)
            {
                if (!string.IsNullOrWhiteSpace(match.Value))
                {
                    value = match.Value;
                    return true;
                }
            }

            value = null;
            return false;
        }

        public static async Task<TestServices> CreateTestServicesInstance()
        {
            // Create our local test node.
            var accountConfig = new AccountConfiguration
            {
                AccountGenerationCount = AccountCount ?? AttributeHelper.GetDefault(() => AccountCount),
                DefaultAccountEtherBalance = AccountBalance ?? AttributeHelper.GetDefault(() => AccountBalance)
            };

            var testNodeServer = new TestNodeServer(accountConfig: accountConfig);

            // Start our local test node.
            await testNodeServer.RpcServer.StartAsync();

            // Create an RPC client for our local test node.
            var serverPort = testNodeServer.RpcServer.ServerPort;
            var localServerUri = new Uri($"http://{IPAddress.Loopback}:{serverPort}");
            var jsonRpcClient = JsonRpcClient.Create(
                localServerUri,
                defaultGasLimit: DefaultGasLimit ?? AttributeHelper.GetDefault(() => DefaultGasLimit),
                defaultGasPrice: DefaultGasPrice ?? AttributeHelper.GetDefault(() => DefaultGasPrice));

            jsonRpcClient.ErrorFormatter = GetExecutionTraceException;

            // Cache our accounts for our test node.
            var accounts = await jsonRpcClient.Accounts();

            // Enable coverage and tracing on the main node.
            await jsonRpcClient.SetCoverageEnabled(true);
            await jsonRpcClient.SetTracingEnabled(true);

            // Return new test services with the main client information we created.
            return new TestServices(jsonRpcClient, testNodeServer, accounts);
        }

        private static void CreateExternalNodeClient()
        {
            // If we have an external test node configured, but it's not instantiated yet, we do that here.
            if (ExternalNodeHost != null && ExternalNodeTestServices == null)
            {
                // Create an RPC client for our external test node.
                var externalServerUri = ExternalNodeHost;
                var externalNodeClient = JsonRpcClient.Create(
                    externalServerUri,
                    defaultGasLimit: DefaultGasLimit ?? AttributeHelper.GetDefault(() => DefaultGasLimit),
                    defaultGasPrice: DefaultGasPrice ?? AttributeHelper.GetDefault(() => DefaultGasPrice));

                // Cache our accounts for our external test node.
                var externalAccounts = externalNodeClient.Accounts().Result;

                // Initialize our external node test services
                ExternalNodeTestServices = new TestServices(externalNodeClient, null, externalAccounts);
            }
        }

        static async Task<Exception> GetExecutionTraceException(IJsonRpcClient rpcClient, JsonRpcError error)
        {
            var executionTrace = await rpcClient.GetExecutionTrace();
            var traceAnalysis = new ExecutionTraceAnalysis(executionTrace);

            // Build our aggregate exception
            var aggregateException = traceAnalysis.GetAggregateException(error.ToException());

            if (aggregateException == null)
            {
                throw new Exception("RPC error occurred with tracing enabled but no exceptions could be found in the trace data. Please report this issue.", error.ToException());
            }

            return aggregateException;
        }


        public static async Task Init(TestContext testContext)
        {
            var unitTestAssembly = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic)
                .Select(a => a.GetType(testContext.FullyQualifiedTestClassName))
                .Where(t => t != null)
                .Select(t => t.Assembly)
                .First();

            ParseAppConfigSettings(unitTestAssembly);

            IsInitialized = true;

            await Task.CompletedTask;
        }

        static readonly ConcurrentBag<string> _reportBlacklist = new ConcurrentBag<string>();

        /// <summary>
        /// Specify solidity files or directories to be ignored from the test report.
        /// </summary>
        public static void HideSolidityFromReport(params string[] directoryOrSolidityFile)
        {
            foreach (var item in directoryOrSolidityFile)
            {
                _reportBlacklist.Add(item);
            }
        }

        public static async Task Cleanup([CallerFilePath]string callerFilePath = null)
        {
            var reportGeneratorExceptions = new List<Exception>();
            try
            {
                _callerFilePath = callerFilePath;
                await CleanupInternal(reportGeneratorExceptions);
            }
            catch (Exception ex)
            {
                reportGeneratorExceptions.Add(ex);
            }

            if (reportGeneratorExceptions.Count > 0)
            {
                var ex = reportGeneratorExceptions[0];
                if (reportGeneratorExceptions.Count > 1)
                {
                    ex = new AggregateException(reportGeneratorExceptions);
                }

                File.WriteAllText($"post-test-exception-{DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture)}.txt", "Post-test exception: " + ex.ToString());
                throw ex;
            }
        }

        static string GetProjectDirectory()
        {
            var projDir = Path.GetDirectoryName(_callerFilePath);

            string path;

            if (Directory.EnumerateFiles(projDir, "*.csproj", SearchOption.TopDirectoryOnly).Any())
            {
                path = Path.GetFullPath(Path.Combine(projDir, REPORT_DIR));
            }
            else
            {
                path = REPORT_DIR;
            }

            MiscUtil.ResetDirectory(path);
            return path;
        }

        static async Task CleanupInternal(List<Exception> catchExceptions)
        {
            var reportOutputDirectory = GetProjectDirectory();

            // Find the generated class field that contains the solc source data
            var sourcesData = GeneratedSolcData.Default.GetSolcData();
            var solidityCompilerVersion = GeneratedSolcData.Default.SolidityCompilerVersion;

            var flattenedCoverageMaps = CoverageMaps.SelectMany(c => c).ToArray();

            var unitTestResults = UnitTestResults.GroupBy(t => t.Namespace).OrderBy(t => t.Key).ToArray();

            // Determine any solidity files or directories that should not be included in the report
            var ignoreSolidityFiles = new List<string>();
            var ignoreSolidityDirectories = new List<string>();
            foreach (var ignorePath in _reportBlacklist)
            {
                // Normalize path separator, and set lowercase
                var normalizedIgnorePath = ignorePath.Replace('\\', '/').Trim('/').ToLowerInvariant();
                if (normalizedIgnorePath.EndsWith(".sol", StringComparison.OrdinalIgnoreCase))
                {
                    ignoreSolidityFiles.Add(normalizedIgnorePath);
                }
                else
                {
                    ignoreSolidityDirectories.Add(normalizedIgnorePath + "/");
                }
            }

            var solcSourceInfos = new List<SolcSourceInfo>();
            var ignoredSourceFiles = new List<string>();
            foreach (var sourceInfo in sourcesData.SolcSourceInfo)
            {
                var lowercaseSourceFile = sourceInfo.FileName.ToLowerInvariant();
                // File is explicitly ignored
                if (ignoreSolidityFiles.Contains(lowercaseSourceFile))
                {
                    ignoredSourceFiles.Add(sourceInfo.FileName);
                    continue;
                }

                // Source file directory is ignored
                if (ignoreSolidityDirectories.Any(d => lowercaseSourceFile.StartsWith(d, StringComparison.Ordinal)))
                {
                    ignoredSourceFiles.Add(sourceInfo.FileName);
                    continue;
                }

                solcSourceInfos.Add(sourceInfo);
            }

            // Generate report from coverage data
            ReportGenerator.CreateReport(
                solidityCompilerVersion,
                flattenedCoverageMaps,
                solcSourceInfos.ToArray(),
                sourcesData.SolcBytecodeInfo,
                unitTestResults,
                ignoredSourceFiles.ToArray(),
                reportOutputDirectory,
                catchExceptions);

            var reportFilePath = Path.GetFullPath(Path.Combine(reportOutputDirectory, ReportGenerator.REPORT_INDEX_FILE));
            var reportFileUri = MiscUtil.GetFileUrl(reportFilePath);
            var reportGeneratedMessage = $"Coverage report generated at:{Environment.NewLine}{reportFileUri}";

            Console.WriteLine(reportGeneratedMessage);
            System.Diagnostics.Debug.WriteLine(reportGeneratedMessage);

            var shouldOpenReport = Environment.GetEnvironmentVariable("OPEN_REPORT") == "TRUE";
            if (shouldOpenReport)
            {
                MiscUtil.OpenBrowser(reportFilePath);
            }

            // Dispose of all test servers.
            // A shutdown can take a couple seconds so start all the shutdown
            // tasks at once, then do the waiting in the Dispose afterwards.
            var testServices = await TestServicesPool.GetItemsAsync();

            foreach (var testService in testServices)
            {
#pragma warning disable CS4014
                testService.TestNodeServer.RpcServer.StopAsync();
#pragma warning restore CS4014
            }

            foreach (var testService in testServices)
            {
                testService.TestNodeServer.Dispose();
            }

            var shouldPause = Environment.GetEnvironmentVariable("PAUSE_ON_COMPLETE") == "TRUE";
            if (shouldPause)
            {
                Console.ReadLine();
                //Process.GetCurrentProcess().WaitForExit();
            }
        }


    }
}
