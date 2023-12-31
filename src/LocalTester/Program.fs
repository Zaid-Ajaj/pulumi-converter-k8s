﻿open System.Collections.Generic
open Converter
open System
open System.IO
open System.Threading.Tasks
open CliWrap
open CliWrap.Buffered
open System.Linq

let pulumiCliBinary() : Task<string> = task {
    try
        // try to get the version of pulumi installed on the system
        let! pulumiVersionResult =
            Cli.Wrap("pulumi")
                .WithArguments("version")
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync()

        let version = pulumiVersionResult.StandardOutput.Trim()
        let versionRegex = Text.RegularExpressions.Regex("v[0-9]+\\.[0-9]+\\.[0-9]+")
        if versionRegex.IsMatch version then
            return "pulumi"
        else
            return! failwith "Pulumi not installed"
    with
    | error ->
        // when pulumi is not installed, try to get the version of of the dev build
        // installed on the system using `make install` in the pulumi repo
        let homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        let pulumiPath = Path.Combine(homeDir, ".pulumi-dev", "bin", "pulumi")
        if File.Exists pulumiPath then
            return pulumiPath
        elif File.Exists $"{pulumiPath}.exe" then
            return $"{pulumiPath}.exe"
        else
            return "pulumi"
}

let convert(workingDirectory: string, targetDirectory: string, targetLanguage: string) = task {
    let! pulumi = pulumiCliBinary()
    let! output =
        Cli.Wrap(pulumi).WithArguments($"convert --from pcl --language {targetLanguage} --out {targetDirectory}")
           .WithWorkingDirectory(workingDirectory)
           .WithValidation(CommandResultValidation.None)
           .ExecuteBufferedAsync()

    if output.ExitCode = 0 then
        return Ok output.StandardOutput
    else
        let! secondAttemptGenerateOnly =
            Cli.Wrap(pulumi).WithArguments($"convert --from pcl --language {targetLanguage} --out {targetDirectory} --generate-only")
               .WithWorkingDirectory(workingDirectory)
               .WithValidation(CommandResultValidation.None)
               .ExecuteBufferedAsync()
        
        if secondAttemptGenerateOnly.ExitCode = 0 then
            return Ok secondAttemptGenerateOnly.StandardOutput
        else
            return Error secondAttemptGenerateOnly.StandardError
}

let rec findParent (directory: string) (fileToFind: string) =
    let path = if Directory.Exists(directory) then directory else Directory.GetParent(directory).FullName
    let files = Directory.GetFiles(path)
    if files.Any(fun file -> Path.GetFileName(file).ToLower() = fileToFind.ToLower())
    then path
    else findParent (DirectoryInfo(path).Parent.FullName) fileToFind

let repositoryRoot = findParent __SOURCE_DIRECTORY__ "README.md"

[<EntryPoint>]
let main (args: string[]) =
    try
        let integrationTests = Path.Combine(repositoryRoot, "integration_tests")
        let directories = Directory.EnumerateDirectories integrationTests
        for example in directories do
            let name = DirectoryInfo(example).Name
            let kubernetesFilePath = Path.Combine(example, $"{name}.yaml")
            if not (File.Exists kubernetesFilePath) then
                printfn $"Couldn't find file at {kubernetesFilePath}"
            else
                let pulumiTargetDirectory = Path.Combine(example, "pulumi_pcl")
                if not (Directory.Exists pulumiTargetDirectory) then
                    Directory.CreateDirectory pulumiTargetDirectory |> ignore
                let inputYamlContent = File.ReadAllText(kubernetesFilePath)
                let errors = ResizeArray()
                let usedNames = Dictionary<string, int>()
                let resources = List.choose id [
                    for document in YamlDocument.parseYamlDocuments inputYamlContent do
                        match YamlDocument.kubeDocument document with
                        | Ok kubeDocument ->
                            let resource = Transform.fromKubeDocument kubeDocument usedNames
                            Some resource
                        | Error parseError ->
                            errors.Add parseError
                            None
                ]
                
                let compilationResult =
                    if errors.Count > 0 then
                        Error (String.Join("\n", errors))
                    else
                        let pulumiProgram : PulumiTypes.PulumiProgram = {
                            nodes = [ for res in resources -> PulumiTypes.PulumiNode.Resource res ]
                        }
                        let outputFile = Path.Combine(pulumiTargetDirectory, $"{name}.pp")
                        let output = Printer.printProgram pulumiProgram
                        File.WriteAllText(outputFile, output)
                        Ok ()

                match compilationResult with
                | Error error ->
                    printfn $"Failed to compile file at {kubernetesFilePath}: %s{error}"
                | Ok () ->
                    printfn $"Converted YAML into Pulumi at {pulumiTargetDirectory}"
                    let targetLanguages = ["typescript"; "csharp"]
                    for targetLanguage in targetLanguages do
                        let targetDirectory = Path.Combine(example, targetLanguage)
                        let conversion =
                            convert (pulumiTargetDirectory, targetDirectory, targetLanguage)
                            |> Async.AwaitTask
                            |> Async.RunSynchronously

                        match conversion with
                        | Error errorMessage ->
                            failwithf $"Failed to convert Pulumi program to {targetLanguage}: {errorMessage}"
                        | Ok _ ->
                            printfn $"Successfully converted {kubernetesFilePath} to {targetLanguage}"
        0
    with
    | ex ->
        printfn $"Error occured: {ex.Message}"
        1