#tool "nuget:?package=GitVersion.CommandLine";
#tool "nuget:?package=NUnit.ConsoleRunner";
#addin "nuget:?package=Cake.XdtTransform";
#addin "nuget:?package=Cake.FileHelpers";
#addin "nuget:?package=Cake.Topshelf"
#addin nuget:?package=Cake.AppVeyor
#addin nuget:?package=Refit&version=3.0.0
#addin nuget:?package=Newtonsoft.Json&version=9.0.1

public void PrintUsage()
{
	Console.WriteLine($"Usage: build.cake [options]{Environment.NewLine}" +
								$"Options:{Environment.NewLine}" +
								$"\t-target\t\t\t\tCake build entry point.\tDefaults to 'BuildOnCommit'.{Environment.NewLine}" +
								$"\t-configuration\t\t\tBuild configuration [Debug|Release]. Defaults to 'Release'.{Environment.NewLine}" +
								$"\t-verbosity\t\t\tVerbosity [Quiet|Minimal|Normal|Verbose|Diagnostic]. Defaults to 'Minimal'.{Environment.NewLine}" +
								$"\t-branch\t\tThe branch being built. Required{Environment.NewLine}");
}

private Verbosity ParseVerbosity(string verbosity)
{
	Verbosity typedVerbosity;
	if(Enum.TryParse<Verbosity>(verbosity, out typedVerbosity)){
		return typedVerbosity;
	}
	return Verbosity.Minimal;
}

private NuGetVerbosity MapVerbosityToNuGetVerbosity(Verbosity verbosity)
{
	switch(verbosity)
	{
		case Verbosity.Diagnostic:
		case Verbosity.Verbose:
			return NuGetVerbosity.Detailed;
		case Verbosity.Minimal:
		case Verbosity.Quiet:
			return NuGetVerbosity.Quiet;
		default:
			return NuGetVerbosity.Normal;
	}
}

public void CmdExecute(string command)
{
	var settings = new ProcessSettings {
		Arguments = "/Q /c \"" + command + "\""
	};

    using(var process = StartAndReturnProcess("cmd", settings))
    {
        process.WaitForExit();
        // This should output 0 as valid arguments supplied
        Information("Exit code: {0}", process.GetExitCode());
        if(process.GetExitCode() != 0)
        {
            throw new Exception("Error executing cmd command");
        }
    }
}

//////////////////////////////////////////////////////////////////////
// ARGUMENTS
//////////////////////////////////////////////////////////////////////

var target = Argument("target", "BuildOnCommit");
var configuration = Argument("configuration", "Release");
var verbosity = ParseVerbosity(Argument("verbosity", "Verbose"));
var buildServerBranch = Argument<string>("branch", string.Empty);

//////////////////////////////////////////////////////////////////////
// PREPARATION
//////////////////////////////////////////////////////////////////////
var solution = "../src/Cake.Jira.sln";
var runningOnBuildServer = AppVeyor.IsRunningOnAppVeyor;
string nugetVersion = null;
string assemblyVersion = null;
GitVersion gitVersion = null;
var runningPullRequestBuild = buildServerBranch.Contains("/merge");
Information($"Running build for branch {buildServerBranch}");
Information(string.Format("runningPullRequestBuild: {0}", runningPullRequestBuild));

//////////////////////////////////////////////////////////////////////
// TASKS
//////////////////////////////////////////////////////////////////////

Task("Clean")
	.Does(() =>
	{
		DotNetBuild(solution,
			settings =>
						settings.SetConfiguration(configuration)
							.SetVerbosity(verbosity)
							.WithTarget("Clean"));
	});

Task("Restore-NuGet-Packages")
	.Does(() =>
	{
    	NuGetRestore(solution, new NuGetRestoreSettings
		{
			Verbosity = MapVerbosityToNuGetVerbosity(verbosity)
		});
	});


Task("Get-GitVersion")
		.WithCriteria(() => runningOnBuildServer && !runningPullRequestBuild)
		.Does(() => {
			gitVersion = GitVersion(new GitVersionSettings
			{
				UpdateAssemblyInfo = false,
				NoFetch = true,
				WorkingDirectory = "../"
			});

			Information($"AssemblySemVer: {gitVersion.AssemblySemVer}{Environment.NewLine}"+
							$"SemVer: {gitVersion.AssemblySemVer}{Environment.NewLine}" +
							$"FullSemVer: {gitVersion.FullSemVer}{Environment.NewLine}" +
							$"MajorMinorPatch: {gitVersion.MajorMinorPatch}{Environment.NewLine}" +
							$"NuGetVersionV2: {gitVersion.NuGetVersionV2}{Environment.NewLine}" +
							$"NuGetVersion: {gitVersion.NuGetVersion}{Environment.NewLine}" +
							$"BranchName: {gitVersion.BranchName}{Environment.NewLine}" +
							$"Sha: {gitVersion.Sha}{Environment.NewLine}" +
							$"Pre-Release Label: {gitVersion.PreReleaseLabel}{Environment.NewLine}" +
							$"Pre-Release Number: {gitVersion.PreReleaseNumber}{Environment.NewLine}" +
							$"Pre-Release Tag: {gitVersion.PreReleaseTag}{Environment.NewLine}" +
							$"Pre-Release Tag with dash: {gitVersion.PreReleaseTagWithDash}{Environment.NewLine}" +
							$"Build MetaData: {gitVersion.BuildMetaData}{Environment.NewLine}" +
							$"Build MetaData Padded: {gitVersion.BuildMetaDataPadded}{Environment.NewLine}" +
							$"Full Build MetaData: {gitVersion.FullBuildMetaData}{Environment.NewLine}");

			if(string.IsNullOrWhiteSpace(gitVersion.PreReleaseTagWithDash))
			{
				Information("No Pre-Release tag found. Versioning as a Release...");
			}

			nugetVersion = gitVersion.NuGetVersion;
			assemblyVersion = gitVersion.AssemblySemVer;

			if(runningOnBuildServer)
			{
				AppVeyor.UpdateBuildVersion(nugetVersion);
			}
		});

Task("Set-Assembly-Information-Files")
	.WithCriteria(() => runningOnBuildServer && !runningPullRequestBuild)
	.IsDependentOn("Get-GitVersion")
	.Does(() => {

		var assemblyInfos = GetFiles("../**/AssemblyVersionInfo.cs");
		foreach(var assemblyInfoPath in assemblyInfos)
		{
			Information($"Found assembly info in {assemblyInfoPath.FullPath}");
			var assemblyInfo = ParseAssemblyInfo(assemblyInfoPath);

			var assemblyInfoSettings = new AssemblyInfoSettings {
				Version = assemblyVersion,
				FileVersion = assemblyVersion,
				InformationalVersion = nugetVersion
			};
			
			CreateAssemblyInfo(assemblyInfoPath, assemblyInfoSettings);
		}
	});

Task("Build")
  	.IsDependentOn("Clean")
  	.IsDependentOn("Restore-NuGet-Packages")
	.IsDependentOn("Set-Assembly-Information-Files")
	.Does(() =>
	{
		DotNetBuild(solution,
			settings =>
						settings.SetConfiguration(configuration)
							.SetVerbosity(verbosity)
							.WithTarget("Build"));
	});

Task("Run-Tests")
	.IsDependentOn("Build")
	.Does(() =>
	{
		var directory  =Directory("./.output");
		EnsureDirectoryExists(directory);

		NUnit3($"../**/bin/{configuration}/*.Tests.dll", new NUnit3Settings
		{
			Configuration = configuration,
			WorkingDirectory = directory.Path
		});
	});

Task("Upload-Test-Results-To-AppVeyor")
   .IsDependentOn("Run-Tests")
   .WithCriteria(runningOnBuildServer)
   .Does(() =>
   {
       AppVeyor.UploadTestResults(
           File("./.output/TestResult.xml").Path,
           AppVeyorTestResultsType.NUnit3);
   });

Task("NuGet-Package")
	.IsDependentOn("Run-Tests")
	.IsDependentOn("Get-GitVersion")
	.WithCriteria(runningOnBuildServer)
	.Does(() => 
	{
		Information(string.Format("Using version {0} for nuget packages", nugetVersion));
		
		var settings = new NuGetPackSettings
		{
			OutputDirectory = "./.nuget",
			Verbosity = NuGetVerbosity.Detailed,
			Properties = new Dictionary<string, string>()
			{
				{"Configuration", configuration}
			},
			Version = nugetVersion
		};
		
		EnsureDirectoryExists(Directory("./.nuget").Path);
		
		var nuspecs = GetFiles("../**/Cake.*.nuspec");
		foreach(var file in nuspecs)
		{
			Information(file.FullPath);
			NuGetPack(file.ToString(), settings);
		}
	});

Task("Upload-NuGet-Packages-To-AppVeyor")
	.IsDependentOn("NuGet-Package")
	.WithCriteria(runningOnBuildServer)
	.Does(() => 
	{
		var packages = GetFiles("./.nuget/Cake.*.nupkg");
		foreach(var package in packages)
		{
			Information(package.FullPath);
			AppVeyor.UploadArtifact(package, new AppVeyorUploadArtifactsSettings()
			{
				ArtifactType = AppVeyorUploadArtifactType.NuGetPackage
			});		
		}
	});

//////////////////////////////////////////////////////////////////////
// TASK TARGETS
//////////////////////////////////////////////////////////////////////

Task("BuildOnCommit")
	.IsDependentOn("Upload-Test-Results-To-AppVeyor")
	.IsDependentOn("Upload-NuGet-Packages-To-AppVeyor")
	.OnError(exception =>
	{
		PrintUsage();
	});

//////////////////////////////////////////////////////////////////////
// EXECUTION
//////////////////////////////////////////////////////////////////////
if (target=="Help")
{
	PrintUsage();
}
else
{
	RunTarget(target);
}
