﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Execution;
using Microsoft.Build.Framework;
using Microsoft.Build.Logging;

namespace JSIL.Compiler {
    public static class SolutionBuilder {
        public class SolutionBuildResult {
            public readonly string[] OutputFiles;
            public readonly BuiltProject[] ProjectsBuilt;
            public readonly string[] TargetFilesUsed;

            public SolutionBuildResult (string[] outputFiles, BuiltProject[] projectsBuilt, string[] targetFiles) {
                OutputFiles = outputFiles;
                ProjectsBuilt = projectsBuilt;
                TargetFilesUsed = targetFiles;
            }
        }

        // The only way to actually specify a solution configuration/platform is by messing around with internal/private types!
        // Using the normal globalProperties method to set configuration/platform will break all the projects inside the
        //  solution by forcibly overriding their configuration/platform. MSBuild is garbage.
        public static ProjectInstance[] ParseSolutionFile (
            string solutionFile, string buildConfiguration, string buildPlatform,
            Dictionary<string, string> globalProperties, BuildManager manager
        ) {
            var asmBuild = manager.GetType().Assembly;

            // Find the types used internally by MSBuild to convert .sln files into MSBuild projects.
            var tSolutionParser = asmBuild.GetType("Microsoft.Build.Construction.SolutionParser", true);
            var tProjectGenerator = asmBuild.GetType("Microsoft.Build.Construction.SolutionProjectGenerator", true);

            // Create an instance of the solution parser. The ctor is internal, hence the second arg.
            var solutionParser = Activator.CreateInstance(tSolutionParser, true);

            var fieldFlags = BindingFlags.Instance | 
                BindingFlags.FlattenHierarchy | 
                BindingFlags.NonPublic | 
                BindingFlags.Public;

            Func<object, string, object> getField = (target, fieldName) =>
                target.GetType().GetField(fieldName, fieldFlags).GetValue(target);

            Action<object, string, object> setField = (target, fieldName, value) =>
                target.GetType().GetField(fieldName, fieldFlags).SetValue(target, value);

            // Point the solution parser instance to the solution file.
            setField(solutionParser, "solutionFile", solutionFile);
            // Parse the solution file. The generator will use the parsed information later.
            solutionParser.GetType().InvokeMember(
                "ParseSolutionFile",
                BindingFlags.Instance | BindingFlags.InvokeMethod | BindingFlags.NonPublic,
                null, solutionParser, new object[0]
            );

            // Override the configuration and platform that may have been selected when parsing the solution
            //  file.
            if (buildConfiguration != null)
                setField(solutionParser, "defaultConfigurationName", buildConfiguration);
            if (buildPlatform != null)
                setField(solutionParser, "defaultPlatformName", buildPlatform);

            // Forces the solution parser to scan project dependencies and select the configuration/platform
            //  that we provided above.
            if ((buildConfiguration != null) || (buildPlatform != null))
                setField(solutionParser, "solutionContainsWebDeploymentProjects", true);

            // The generator needs a logging service and build context.
            var loggingService = manager.GetType().InvokeMember(
                "Microsoft.Build.BackEnd.IBuildComponentHost.get_LoggingService",
                BindingFlags.Instance | BindingFlags.InvokeMethod | BindingFlags.NonPublic,
                null, manager, new object[0]
            );
            var context = new BuildEventContext(0, 0, 0, 0);

            // Convert the parsed solution into one or more project instances that we can build.
            var result = tProjectGenerator.InvokeMember(
                "Generate", 
                BindingFlags.Static | BindingFlags.InvokeMethod | BindingFlags.NonPublic,
                null, null, new object[] {
                    solutionParser, 
                    globalProperties,
                    null,
                    context,
                    loggingService
                }
            );

            return (ProjectInstance[])result;
        }

        public static SolutionBuildResult Build (string solutionFile, string buildConfiguration = null, string buildPlatform = null, string buildTarget = "Build", string logVerbosity = null) {
            string configString = String.Format("{0}|{1}", buildConfiguration ?? "<default>", buildPlatform ?? "<default>");

            if ((buildConfiguration ?? buildPlatform) != null)
                Console.Error.WriteLine("// Running target '{2}' of '{0}' ({1}) ...", Program.ShortenPath(solutionFile), configString, buildTarget);
            else
                Console.Error.WriteLine("// Running target '{1}' of '{0}' ...", Program.ShortenPath(solutionFile), buildTarget);

            var pc = new ProjectCollection();
            var parms = new BuildParameters(pc);
            var globalProperties = new Dictionary<string, string>();

            var hostServices = new HostServices();
            var eventRecorder = new BuildEventRecorder();
            LoggerVerbosity _logVerbosity;

            if ((logVerbosity == null) || !Enum.TryParse(logVerbosity, out _logVerbosity))
                _logVerbosity = LoggerVerbosity.Quiet;

            parms.Loggers = new ILogger[] { 
                new ConsoleLogger(_logVerbosity), eventRecorder
            };

            var manager = BuildManager.DefaultBuildManager;

            Console.Error.Write("// Generating MSBuild projects for solution '{0}'...", Path.GetFileName(solutionFile));
            // Begin a fake build so the manager has a logger available.
            manager.BeginBuild(parms);

            var projects = ParseSolutionFile(
                solutionFile, buildConfiguration, buildPlatform,
                globalProperties, manager
            );

            manager.EndBuild();
            Console.Error.WriteLine(" {0} project(s) generated.", projects.Length);

            if (File.ReadAllText(solutionFile).Contains("ProjectSection(ProjectDependencies)")) {
                Console.Error.WriteLine("// WARNING: Your solution file contains project dependencies. MSBuild ignores these, so your build may fail. If it does, try building it in Visual Studio first to resolve the dependencies.");
            }

            var resultFiles = new HashSet<string>();
            foreach (var project in projects) {

                // Save out the generated msbuild project for each solution, to aid debugging.
                try {
                    project.ToProjectRootElement().Save(project.FullPath, Encoding.UTF8);
                } catch (Exception exc) {
                    Console.Error.WriteLine("// Failed to save generated project '{0}': {1}", Path.GetFileName(project.FullPath), exc.Message);
                }

                Console.Error.WriteLine("// Building project '{0}'...", project.FullPath);

                var request = new BuildRequestData(
                    project, new string[] { buildTarget }, 
                    hostServices, BuildRequestDataFlags.None
                );

                BuildResult result = null;
                try {
                    result = manager.Build(parms, request);
                } catch (Exception exc) {
                    Console.Error.WriteLine("// Compilation failed: {0}", exc.Message);
                    continue;
                }

                foreach (var kvp in result.ResultsByTarget) {
                    var targetResult = kvp.Value;

                    if ((targetResult.Exception != null) || (targetResult.ResultCode == TargetResultCode.Failure)) {
                        string errorMessage = "Unknown error";
                        if (targetResult.Exception != null)
                            errorMessage = targetResult.Exception.Message;
                        Console.Error.WriteLine("// Compilation failed for target '{0}': {1}", kvp.Key, errorMessage);
                    } else if (targetResult.Items.Length > 0) {
                        Console.Error.WriteLine("// Target '{0}' produced {1} output(s).", kvp.Key, targetResult.Items.Length);

                        foreach (var filename in targetResult.Items)
                            resultFiles.Add(filename.ItemSpec);
                    }
                }
            }

            return new SolutionBuildResult(
                resultFiles.ToArray(),
                eventRecorder.ProjectsById.Values.ToArray(),
                eventRecorder.TargetFiles.ToArray()
            );
        }
    }

    public class BuiltProject {
        public BuiltProject Parent;
        public int Id;
        public string File;

        public override string ToString () {
            return String.Format("{0} '{1}'", Id, File);
        }
    }

    public class BuildEventRecorder : ILogger {
        public readonly Dictionary<int, BuiltProject> ProjectsById = new Dictionary<int, BuiltProject>();
        public readonly HashSet<string> TargetFiles = new HashSet<string>(); 

        public void Initialize (IEventSource eventSource) {
            eventSource.ProjectStarted += (sender, args) => {
                var parentId = args.ParentProjectBuildEventContext.ProjectInstanceId;

                BuiltProject parentProject;
                ProjectsById.TryGetValue(parentId, out parentProject);

                var obj = new BuiltProject {
                    Parent = parentProject,
                    Id = args.ProjectId,
                    File = args.ProjectFile
                };

                ProjectsById[args.ProjectId] = obj;
            };
            eventSource.TargetStarted += (sender, args) =>
                TargetFiles.Add(args.TargetFile);
        }

        public string Parameters {
            get;
            set;
        }

        public void Shutdown () {
        }

        public LoggerVerbosity Verbosity {
            get;
            set;
        }
    }
}
