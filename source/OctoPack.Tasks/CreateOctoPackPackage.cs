﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using OctoPack.Tasks.Util;

namespace OctoPack.Tasks
{
	/// <summary>
	/// An MSBuild task that creates an Octopus Deploy package containing only the appropriate files - 
	/// for example, an ASP.NET website will contain only the content files, assets, binaries and configuration
	/// files. C# files won't be included. Other project types (console applications, Windows Services, etc.) will 
	/// only contain the binaries. 
	/// </summary>
	public class CreateOctoPackPackage : AbstractTask
	{
		private readonly IOctopusFileSystem fileSystem;
		private readonly Hashtable seenBefore = new Hashtable(StringComparer.OrdinalIgnoreCase); 
		
		public CreateOctoPackPackage() : this(new OctopusPhysicalFileSystem())
		{
		}

		public CreateOctoPackPackage(IOctopusFileSystem fileSystem)
		{
			this.fileSystem = fileSystem;
		}

		/// <summary>
		/// Allows the name of the NuSpec file to be overridden. If empty, defaults to <see cref="ProjectName"/>.nuspec.
		/// </summary>
		public string NuSpecFileName { get; set; }

		/// <summary>
		/// Appends the value to <see cref="ProjectName"/> when generating the Id of the Nuget Package
		/// </summary>
		public string AppendToPackageId { get; set; }

		/// <summary>
		/// The list of content files in the project. For web applications, these files will be included in the final package.
		/// </summary>
		[Required]
		public ITaskItem[] ContentFiles { get; set; }

		/// <summary>
		/// The list of written files in the project. This should mean all binaries produced from the build.
		/// </summary>
		[Required]
		public ITaskItem[] WrittenFiles { get; set; }

		/// <summary>
		/// The projects root directory; set to <code>$(MSBuildProjectDirectory)</code> by default.
		/// </summary>
		[Required]
		public string ProjectDirectory { get; set; }

		/// <summary>
		/// The directory in which the built files were written to.
		/// </summary>
		[Required]
		public string OutDir { get; set; }

		/// <summary>
		/// Whether TypeScript (.ts) files should be included.
		/// </summary>
		public bool IncludeTypeScriptSourceFiles { get; set; }

		/// <summary>
		/// The NuGet package version. If not set via an MSBuild property, it will be empty in which case we'll use the version in the NuSpec file or 1.0.0.
		/// </summary>
		public string PackageVersion { get; set; }

		/// <summary>
		/// The name of the project; by default will be set to $(MSBuildProjectName). 
		/// </summary>
		[Required]
		public string ProjectName { get; set; }

		/// <summary>
		/// The path to the primary DLL/executable being produced by the project.
		/// </summary>
		[Required]
		public string PrimaryOutputAssembly { get; set; }

		/// <summary>
		/// Allows release notes to be attached to the NuSpec file when building.
		/// </summary>
		public string ReleaseNotesFile { get; set; }

		public string AppConfigFile { get; set; }

		/// <summary>
		/// Used to output the list of built packages.
		/// </summary>
		[Output]
		public ITaskItem[] Packages { get; set; }

		/// <summary>
		/// The path to NuGet.exe.
		/// </summary>
		[Output]
		public string NuGetExePath { get; set; }


		public bool EnforceAddingFiles { get; set; }

		public bool PublishPackagesToTeamCity { get; set; }        

		/// <summary>
		/// Extra arguments to pass along to nuget.
		/// </summary>
		public string NuGetArguments { get; set; }

		/// <summary>
		/// Properties to pass along to nuget
		/// </summary>
		[Output]
		public string NuGetProperties { get; set; }

		/// <summary>
		/// Whether to suppress the warning about having scripts at the root
		/// </summary>
		public bool IgnoreNonRootScripts { get; set; }

		public override bool Execute()
		{
			try
			{
				LogDiagnostics();

				FindNuGet();

				WrittenFiles = WrittenFiles ?? new ITaskItem[0];
				LogMessage("Written files: " + WrittenFiles.Length);

				var octopacking = CreateEmptyOutputDirectory("octopacking");
				var octopacked = CreateEmptyOutputDirectory("octopacked");

				var specFilePath = GetOrCreateNuSpecFile(octopacking);
				var specFile = OpenNuSpecFile(specFilePath);

				UpdatePackageIdWithAppendValue(specFile);

				AddReleaseNotes(specFile);

				OutDir = fileSystem.GetFullPath(OutDir);

				if (SpecAlreadyHasFiles(specFile) && EnforceAddingFiles == false)
				{
					LogMessage("Files will not be added because the NuSpec file already contains a <files /> section with one or more elements and option OctoPackEnforceAddingFiles was not specified.", MessageImportance.High);
				}

				if (SpecAlreadyHasFiles(specFile) == false || EnforceAddingFiles)
				{
					var content =
						from file in ContentFiles
						where !string.Equals(Path.GetFileName(file.ItemSpec), "packages.config", StringComparison.OrdinalIgnoreCase)
						select file;

					var binaries =
						from file in WrittenFiles
						select file;

					if (IsWebApplication())
					{
						LogMessage("Packaging an ASP.NET web application (Web.config detected)");

						LogMessage("Add content files", MessageImportance.Normal);
						AddFiles(specFile, content, ProjectDirectory);

						LogMessage("Add binary files to the bin folder", MessageImportance.Normal);
						AddFiles(specFile, binaries, ProjectDirectory, relativeTo: OutDir, targetDirectory: "bin");
					}
					else
					{
						LogMessage("Packaging a console or Window Service application (no Web.config detected)");

						LogMessage("Add binary files", MessageImportance.Normal);
						AddFiles(specFile, binaries, ProjectDirectory, relativeTo: OutDir);
					}
				}

				SaveNuSpecFile(specFilePath, specFile);

				RunNuGet(specFilePath,
					octopacking,
					octopacked,
					ProjectDirectory
					);

				CopyBuiltPackages(octopacked);

				LogMessage("OctoPack successful");

				return true;                
			}
			catch (Exception ex)
			{
				LogError("OCT" + ex.GetType().Name.GetHashCode(), ex.Message);
				LogError("OCT" + ex.GetType().Name.GetHashCode(), ex.ToString());
				return false;
			}
		}

		private void LogDiagnostics()
		{
			LogMessage("---Arguments---", MessageImportance.Low);
			LogMessage("Content files: " + (ContentFiles ?? new ITaskItem[0]).Length, MessageImportance.Low);
			LogMessage("ProjectDirectory: " + ProjectDirectory, MessageImportance.Low);
			LogMessage("OutDir: " + OutDir, MessageImportance.Low);
			LogMessage("PackageVersion: " + PackageVersion, MessageImportance.Low);
			LogMessage("ProjectName: " + ProjectName, MessageImportance.Low);
			LogMessage("PrimaryOutputAssembly: " + PrimaryOutputAssembly, MessageImportance.Low);
			LogMessage("NugetArguments: " + NuGetArguments, MessageImportance.Low);
			LogMessage("NugetProperties: " + NuGetProperties, MessageImportance.Low);
			LogMessage("---------------", MessageImportance.Low);
		}

		private string CreateEmptyOutputDirectory(string name)
		{
			var temp = Path.Combine(Path.Combine(ProjectDirectory, "obj"), name);
			LogMessage("Create directory: " + temp, MessageImportance.Low);
			fileSystem.PurgeDirectory(temp, DeletionOptions.TryThreeTimes);
			fileSystem.EnsureDirectoryExists(temp);
			fileSystem.EnsureDiskHasEnoughFreeSpace(temp);
			return temp;
		}

		private string GetOrCreateNuSpecFile(string octopacking)
		{
			var specFileName = NuSpecFileName;
			if (StringHelper.IsNullOrWhiteSpace(specFileName))
			{
				specFileName = RemoveTrailing(ProjectName, ".csproj", ".vbproj") + ".nuspec";
			}

			if (fileSystem.FileExists(specFileName))
				Copy(new[] { Path.Combine(ProjectDirectory, specFileName) }, ProjectDirectory, octopacking);

			var specFilePath = Path.Combine(octopacking, specFileName);
			if (fileSystem.FileExists(specFilePath))
				return specFilePath;

			var packageId = RemoveTrailing(ProjectName, ".csproj", ".vbproj");

			LogMessage(string.Format("A NuSpec file named '{0}' was not found in the project root, so the file will be generated automatically. However, you should consider creating your own NuSpec file so that you can customize the description properly.", specFileName));

			#region create nuspec xml
			var manifest = new XmlDocument();
			XmlNode docNode = manifest.CreateXmlDeclaration("1.0", "UTF-8", null);
			manifest.AppendChild(docNode);

			var packageElement = XmlElementExtensions.AddChildElement(manifest, "package");
			var metadata = XmlElementExtensions.AddChildElement(manifest, packageElement, "metadata");

			XmlElementExtensions.AddChildElement(manifest, metadata, "id", packageId);
			XmlElementExtensions.AddChildElement(manifest, metadata, "version", PackageVersion);
			XmlElementExtensions.AddChildElement(manifest, metadata, "authors", Environment.UserName);
			XmlElementExtensions.AddChildElement(manifest, metadata, "owners", Environment.UserName);
			XmlElementExtensions.AddChildElement(manifest, metadata, "licenseUrl", "http://example.com");
			XmlElementExtensions.AddChildElement(manifest, metadata, "projectUrl", "http://example.com");
			XmlElementExtensions.AddChildElement(manifest, metadata, "requireLicenseAcceptance", "false");
			XmlElementExtensions.AddChildElement(manifest, metadata, "description", "The " + ProjectName + " deployment package, built on " + DateTime.Now.ToShortDateString());
			XmlElementExtensions.AddChildElement(manifest, metadata, "releaseNotes", "");
			manifest.Save(specFilePath);
			#endregion

			return specFilePath;
		}

		private string RemoveTrailing(string specFileName, params string[] extensions)
		{
			foreach (var extension in extensions)
			{
				if (specFileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
				{
					specFileName = specFileName.Substring(0, specFileName.Length - extension.Length).TrimEnd('.');
				}
			}

			return specFileName;
		}

		private XmlDocument OpenNuSpecFile(string specFilePath)
		{
			var xml = fileSystem.ReadFile(specFilePath);
			var document = new XmlDocument();
			document.LoadXml(xml);
			return document;
		}

		private void AddReleaseNotes(XmlDocument nuSpec)
		{
			if (StringHelper.IsNullOrWhiteSpace(ReleaseNotesFile))
			{
				return;
			}

			ReleaseNotesFile = fileSystem.GetFullPath(ReleaseNotesFile);

			if (!fileSystem.FileExists(ReleaseNotesFile))
			{
				LogWarning("OCT901", string.Format("The release notes file: {0} does not exist or could not be found. Release notes will not be added to the package.", ReleaseNotesFile));
				return;
			}

			LogMessage("Adding release notes from file: " + ReleaseNotesFile);

			var notes = fileSystem.ReadFile(ReleaseNotesFile);

			var package = XmlElementExtensions.ElementAnyNamespace(nuSpec, "package");
			if (package == null) throw new Exception("The NuSpec file does not contain a <package> XML element. The NuSpec file appears to be invalid.");

			var metadata = XmlElementExtensions.ElementAnyNamespace(package, "metadata");
			if (metadata == null) throw new Exception("The NuSpec file does not contain a <metadata> XML element. The NuSpec file appears to be invalid.");

			var releaseNotes = XmlElementExtensions.ElementAnyNamespace(metadata, "releaseNotes");
			if (releaseNotes == null)
			{
				XmlElementExtensions.AddChildElement(nuSpec, metadata, "releaseNotes", notes);
			}
			else
			{
				releaseNotes.InnerText = notes;
			}
		}

		private void UpdatePackageIdWithAppendValue(XmlDocument nuSpec)
		{
			if (StringHelper.IsNullOrWhiteSpace(AppendToPackageId))
			{
				return;
			}

			var package = XmlElementExtensions.ElementAnyNamespace(nuSpec, "package");
			if (package == null) throw new Exception("The NuSpec file does not contain a <package> XML element. The NuSpec file appears to be invalid.");

			var metadata = XmlElementExtensions.ElementAnyNamespace(package, "metadata");
			if (metadata == null) throw new Exception("The NuSpec file does not contain a <metadata> XML element. The NuSpec file appears to be invalid.");

			var packageId = XmlElementExtensions.ElementAnyNamespace(metadata, "id");
			if (packageId == null) throw new Exception("The NuSpec file does not contain a <id> XML element. The NuSpec file appears to be invalid.");

			packageId.InnerText = string.Format("{0}.{1}", packageId.InnerText, AppendToPackageId.Trim());
		}


		private void AddFiles(XmlDocument nuSpec, IEnumerable<ITaskItem> sourceFiles, string sourceBaseDirectory, string targetDirectory = "", string relativeTo = "")
		{

			var package = XmlElementExtensions.ElementAnyNamespace(nuSpec, "package");
			if (package == null) throw new Exception("The NuSpec file does not contain a <package> XML element. The NuSpec file appears to be invalid.");


			var files = XmlElementExtensions.ElementAnyNamespace(package, "files") ??
						XmlElementExtensions.AddChildElement(nuSpec, package, "files");

			if (!StringHelper.IsNullOrWhiteSpace(relativeTo) && Path.IsPathRooted(relativeTo))
			{
				relativeTo = fileSystem.GetPathRelativeTo(relativeTo, sourceBaseDirectory);
			}

			foreach (var sourceFile in sourceFiles)
			{
				
				var destinationPath = sourceFile.ItemSpec;
				var link = sourceFile.GetMetadata("Link");
				if (!StringHelper.IsNullOrWhiteSpace(link))
				{
					destinationPath = link;
				}

				if (!Path.IsPathRooted(destinationPath))
				{
					destinationPath = fileSystem.GetFullPath(Path.Combine(sourceBaseDirectory, destinationPath));
				}

				if (Path.IsPathRooted(destinationPath))
				{
					destinationPath = fileSystem.GetPathRelativeTo(destinationPath, sourceBaseDirectory);
				}

				if (!StringHelper.IsNullOrWhiteSpace(relativeTo))
				{
					if (destinationPath.StartsWith(relativeTo, StringComparison.OrdinalIgnoreCase))
					{
						destinationPath = destinationPath.Substring(relativeTo.Length);
					}
				}

				destinationPath = Path.Combine(targetDirectory, destinationPath);

				var sourceFilePath = Path.Combine(sourceBaseDirectory, sourceFile.ItemSpec);

				sourceFilePath = Path.GetFullPath(sourceFilePath);

				if (!fileSystem.FileExists(sourceFilePath))
				{
					LogMessage("The source file '" + sourceFilePath + "' does not exist, so it will not be included in the package", MessageImportance.High);
					continue;
				}

				if (seenBefore.Contains(sourceFilePath))
				{
					continue;
				}

				seenBefore.Add(sourceFilePath, true);

				var fileName = Path.GetFileName(destinationPath);
				if (string.Equals(fileName, "app.config", StringComparison.OrdinalIgnoreCase))
				{
					if (fileSystem.FileExists(AppConfigFile))
					{
						var configFileName = Path.GetFileName(AppConfigFile);
						destinationPath = Path.GetDirectoryName(destinationPath);
						destinationPath = Path.Combine(destinationPath, configFileName);
						

						XmlElementExtensions.AddChildElement(nuSpec, files, "file", new List<XmlNodeAttribute>{ 
							new XmlNodeAttribute { Name = "src", Value = AppConfigFile},
							new XmlNodeAttribute {Name = "target", Value = destinationPath}
						} );

						LogMessage("Added file: " + destinationPath, MessageImportance.Normal);                        
					}
					continue;
				}

				if (new[] {"Deploy.ps1", "DeployFailed.ps1", "PreDeploy.ps1", "PostDeploy.ps1"}.Any(f => string.Equals(f, fileName, StringComparison.OrdinalIgnoreCase)))
				{
					var isNonRoot = destinationPath.Contains('\\') || destinationPath.Contains('/');
					if (isNonRoot && !IgnoreNonRootScripts)
					{
						LogWarning("OCTNONROOT", "As of Octopus Deploy 2.4, PowerShell scripts that are not at the root of the package will not be executed. The script '" + destinationPath + "' lives in a subdirectory, so it will not be executed. If you want Octopus to execute this script, move it to the root of your project. If you don't want it to be executed, you can ignore this warning, or suppress it by setting the MSBuild property OctoPackIgnoreNonRootScripts=true");
					}
				}

				var isTypeScript = string.Equals(Path.GetExtension(sourceFilePath), ".ts", StringComparison.OrdinalIgnoreCase);
				if (isTypeScript)
				{
					if (IncludeTypeScriptSourceFiles)
					{
						XmlElementExtensions.AddChildElement(nuSpec, files, "file", new List<XmlNodeAttribute>{ 
							new XmlNodeAttribute { Name = "src", Value = sourceFilePath},
							new XmlNodeAttribute {Name = "target", Value = destinationPath}
						});
		
						LogMessage("Added file: " + destinationPath, MessageImportance.Normal);
					}

					var changedSource = Path.ChangeExtension(sourceFilePath, ".js");
					var changedDestination = Path.ChangeExtension(destinationPath, ".js");
					if (fileSystem.FileExists(changedSource))
					{
						XmlElementExtensions.AddChildElement(nuSpec, files, "file", new List<XmlNodeAttribute>{ 
							new XmlNodeAttribute { Name = "src", Value = changedSource},
							new XmlNodeAttribute {Name = "target", Value = changedDestination}
						});

						LogMessage("Added file: " + changedDestination, MessageImportance.Normal);
					}
				}
				else
				{
					XmlElementExtensions.AddChildElement(nuSpec, files, "file", new List<XmlNodeAttribute>{ 
							new XmlNodeAttribute { Name = "src", Value = sourceFilePath},
							new XmlNodeAttribute {Name = "target", Value = destinationPath}
						});

					LogMessage("Added file: " + destinationPath, MessageImportance.Normal);
				}
			}
		}

		private static bool SpecAlreadyHasFiles(XmlDocument nuSpec)
		{
			var package = XmlElementExtensions.ElementAnyNamespace(nuSpec, "package");
			if (package == null) throw new Exception("The NuSpec file does not contain a <package> XML element. The NuSpec file appears to be invalid.");

			var files = XmlElementExtensions.ElementAnyNamespace(package, "files");
			return files != null && files.HasChildNodes;
		}

		private void SaveNuSpecFile(string specFilePath, XmlDocument document)
		{
			fileSystem.OverwriteFile(specFilePath, document.InnerXml);
		}

		private bool IsWebApplication()
		{
			return fileSystem.FileExists("web.config");
		}
		
		private void Copy(IEnumerable<string> sourceFiles, string baseDirectory, string destinationDirectory)
		{
			foreach (var source in sourceFiles)
			{
				var relativePath = fileSystem.GetPathRelativeTo(source, baseDirectory);
				var destination = Path.Combine(destinationDirectory, relativePath);

				LogMessage("Copy file: " + source, importance: MessageImportance.Normal);

				var relativeDirectory = Path.GetDirectoryName(destination);
				fileSystem.EnsureDirectoryExists(relativeDirectory);

				fileSystem.CopyFile(source, destination);
			}
		}

		private void FindNuGet()
		{
			if (StringHelper.IsNullOrWhiteSpace(NuGetExePath) || !fileSystem.FileExists(NuGetExePath))
			{
				var nuGetPath = Path.Combine(Path.GetDirectoryName(AssemblyExtensions.FullLocalPath(typeof(CreateOctoPackPackage).Assembly)), "NuGet.exe");
				NuGetExePath = nuGetPath;
			}
		}

		private void RunNuGet(string specFilePath, string octopacking, string octopacked, string projectDirectory)
		{
			var commandLine = "pack \"" + specFilePath + "\"  -NoPackageAnalysis -BasePath \"" + projectDirectory + "\" -OutputDirectory \"" + octopacked + "\"";
			if (!StringHelper.IsNullOrWhiteSpace(PackageVersion))
			{
				commandLine += " -Version " + PackageVersion;
			}

			if (!StringHelper.IsNullOrWhiteSpace(NuGetProperties))
			{
				commandLine += " -Properties " + NuGetProperties;
			}

			if (!StringHelper.IsNullOrWhiteSpace(NuGetArguments)) {
				commandLine += " " + NuGetArguments;
			}

			LogMessage("NuGet.exe path: " + NuGetExePath, MessageImportance.Low);
			LogMessage("Running NuGet.exe with command line arguments: " + commandLine, MessageImportance.Low);

			var exitCode = SilentProcessRunner.ExecuteCommand(
				NuGetExePath,
				commandLine,
				octopacking,
				output => LogMessage(output),
				error => LogError("OCTONUGET", error));

			if (exitCode != 0)
			{
				throw new Exception(string.Format("There was an error calling NuGet. Please see the output above for more details. Command line: '{0}' {1}", NuGetExePath, commandLine));
			}
		}

		private void CopyBuiltPackages(string packageOutput)
		{
			var packageFiles = new List<ITaskItem>();

			foreach (var file in fileSystem.EnumerateFiles(packageOutput, "*.nupkg"))
			{
				LogMessage("Packaged file: " + file, MessageImportance.Low);

				var fullPath = Path.Combine(packageOutput, file);
				packageFiles.Add(CreateTaskItemFromPackage(fullPath));

				Copy(new[] { file }, packageOutput, OutDir);

				if (PublishPackagesToTeamCity && !StringHelper.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TEAMCITY_VERSION")))
				{
					LogMessage("##teamcity[publishArtifacts '" + file + "']");
				}
			}

			LogMessage("Packages have been copied to: " + OutDir, MessageImportance.Low);

			Packages = packageFiles.ToArray();
		}

		private static TaskItem CreateTaskItemFromPackage(string packageFile)
		{
			var metadata = new Hashtable
			{
				{"Name", Path.GetFileName(packageFile)}
			};
			
			return new TaskItem(packageFile, metadata);
		}
	}
}
