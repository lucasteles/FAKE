﻿module Fake.Runtime.FakeRuntime

open System
open System.IO
open Fake.Runtime
open Fake.Runtime.Runners
open Fake.Runtime.Trace
open Paket
open System

type AssemblyData =
  { IsReferenceAssembly : bool
    Info : Runners.AssemblyInfo }

[<RequireQualifiedAccess>]
type DependencyFile =
    | Assembly of AssemblyData
    | Library of Runners.NativeLibrary
    member x.Location =
        match x with
        | Assembly ass -> ass.Info.Location
        | Library lib -> lib.File

let internal filterValidAssembly (logLevel:VerboseLevel) (isSdk, isReferenceAssembly, fi:FileInfo) =
    let fullName = fi.FullName
    try let assembly = Mono.Cecil.AssemblyDefinition.ReadAssembly fullName
        { IsReferenceAssembly = isReferenceAssembly
          Info =
            { Runners.AssemblyInfo.FullName = assembly.Name.FullName
              Runners.AssemblyInfo.Version = assembly.Name.Version.ToString()
              Runners.AssemblyInfo.Location = fullName } } |> Some
    with e ->
        if logLevel.PrintVerbose then Trace.log <| sprintf "Could not load '%s': %O" fullName e
        None

let paketCachingProvider (config:FakeConfig) cacheDir (paketApi:Paket.Dependencies) (paketDependenciesFile:Lazy<Paket.DependenciesFile>) group =
  use __ = Fake.Profile.startCategory Fake.Profile.Category.Paket
  let logLevel = config.VerboseLevel
  let script = config.ScriptFilePath
  let groupStr = match group with Some g -> g | None -> "Main"
  let groupName = Paket.Domain.GroupName (groupStr)
#if DOTNETCORE
  //let framework = Paket.FrameworkIdentifier.DotNetCoreApp (Paket.DotNetCoreAppVersion.V2_0)
  let framework = Paket.FrameworkIdentifier.DotNetStandard (Paket.DotNetStandardVersion.V2_0)
#else
  let framework = Paket.FrameworkIdentifier.DotNetFramework (Paket.FrameworkVersion.V4_6)
#endif
  let lockFilePath = Paket.DependenciesFile.FindLockfile paketApi.DependenciesFile
  let parent s = Path.GetDirectoryName s
  let comb name s = Path.Combine(s, name)
  let dependencyCacheHashFile = Path.Combine(cacheDir, "dependencies.cached")
  let dependencyCacheFile = Path.Combine(cacheDir, "dependencies.txt")

#if DOTNETCORE
  let getCurrentSDKReferenceFiles() =
    // We need use "real" reference assemblies as using the currently running runtime assemlies doesn't work:
    // see https://github.com/fsharp/FAKE/pull/1695

    // Therefore we download the reference assemblies (the NETStandard.Library package)
    // and add them in addition to what we have resolved, 
    // we use the sources in the paket.dependencies to give the user a chance to overwrite.

    // Note: This package/version needs to updated together with our "framework" variable below and needs to 
    // be compatible with the runtime we are currently running on.
    let rootDir = Directory.GetCurrentDirectory()
    let packageName = Domain.PackageName("NETStandard.Library")
    let version = SemVer.Parse("2.0.3")
    let existingpkg = NuGetCache.GetTargetUserNupkg packageName version
    let extractedFolder =
      if File.Exists existingpkg then
        // Shortcut in order to prevent requests to nuget sources if we have it downloaded already
        Path.GetDirectoryName existingpkg
      else
        let sources = paketDependenciesFile.Value.Groups.[groupName].Sources
        let versions =
          Paket.NuGet.GetVersions false None rootDir (PackageResolver.GetPackageVersionsParameters.ofParams sources groupName packageName)
          |> Async.RunSynchronously
          |> dict
        let source =
          match versions.TryGetValue(version) with
          | true, v when v.Length > 0 -> v |> Seq.head
          | _ -> failwithf "Could not find package '%A' with version '%A' in any package source of group '%A', but fake needs this package to compile the script" packageName version groupName    
        
        let _, extractedFolder =
          Paket.NuGet.DownloadAndExtractPackage
            (None, rootDir, false, PackagesFolderGroupConfig.NoPackagesFolder,
             source, [], Paket.Constants.MainDependencyGroup,
             packageName, version, PackageResolver.ResolvedPackageKind.Package, false, false, false, false)
          |> Async.RunSynchronously
        extractedFolder
    let sdkDir = Path.Combine(extractedFolder, "build", "netstandard2.0", "ref")
    Directory.GetFiles(sdkDir, "*.dll")
    |> Seq.toList
#endif


  let writeIntellisenseFile cacheDir =
    let intellisenseFile = Path.Combine (cacheDir, Runners.loadScriptName)
    if logLevel.PrintVerbose then Trace.log <| sprintf "Writing '%s'" intellisenseFile

    // Make sure to create #if !FAKE block, because we don't actually need it.
    let intellisenseContents =
      [| "// This file is automatically generated by FAKE"
         "// This file is needed for IDE support only"
         "#if !FAKE"
         sprintf "#load \"%s\"" Runners.loadScriptLazyName
         "#endif" |]
    File.WriteAllLines (intellisenseFile, intellisenseContents)
    
  let writeIntellisenseLazyFile cacheDir (context : Paket.LoadingScripts.ScriptGeneration.PaketContext) =
    // Write loadDependencies file (basically only for editor support)
    async {
      try
        let intellisenseLazyFile = Path.Combine (cacheDir, Runners.loadScriptLazyName)
        let cache = context.Cache
        cache.StartSetupGroup(groupName) |> ignore
        do! cache.AwaitFinishSetup()
        let groupScripts = Paket.LoadingScripts.ScriptGeneration.generateScriptContent context
        let _, groupScript =
          match groupScripts with
          | [] -> failwith "generateScriptContent returned []"
          | [h] -> failwithf "generateScriptContent returned a single item: %A" h
          | [ _, scripts; _, [groupScript] ] -> scripts, groupScript
          | _ -> failwithf "generateScriptContent returned %A" groupScripts


        let rootDir = DirectoryInfo cacheDir
        let content = groupScript.RenderDirect rootDir (FileInfo intellisenseLazyFile)

        let intellisenseContents =
          [| "// This file is automatically generated by FAKE"
             "// This file is needed for IDE support only"
             content |]
        File.WriteAllLines (intellisenseLazyFile, intellisenseContents)
      with e ->
          eprintfn "Failed to write intellisense script: %O" e
    }

  let lockFile = lazy LockFile.LoadFrom(lockFilePath.FullName)
  let cache = lazy DependencyCache(lockFile.Value)
  let retrieveInfosUncached () =
    match lockFile.Value.Groups |> Map.tryFind groupName with
    | Some g -> ()
    | None -> failwithf "The group '%s' was not found in the lockfile. You might need to run 'paket install' first!" groupName.Name
    
    let (cache:DependencyCache) = cache.Value
    let orderedGroup = cache.OrderedGroups groupName // lockFile.GetGroup groupName

    let rid =
#if DOTNETCORE
        let ridString = Microsoft.DotNet.PlatformAbstractions.RuntimeEnvironment.GetRuntimeIdentifier()
#else
        let ridString = "win"
#endif
        Paket.Rid.Of(ridString)

    // get runtime graph
    let graph =
      async {
        if logLevel.PrintVerbose then Trace.log <| sprintf "Calculating the runtime graph..."
        use runtimeGraphProfile = Fake.Profile.startCategory Fake.Profile.Category.PaketRuntimeGraph
        let result =
          orderedGroup
          |> Seq.choose (fun p ->
            let r = RuntimeGraph.getRuntimeGraphFromNugetCache cacheDir (Some PackagesFolderGroupConfig.NoPackagesFolder) groupName p.Resolved
            if logLevel.PrintVerbose && r.IsSome then
                printfn "Loaded runtime json from: %s-%s" p.Name.CompareString p.Version.AsString
            r)
          |> RuntimeGraph.mergeSeq
        runtimeGraphProfile.Dispose()

        return result
      }
      |> Async.StartAsTask

    // Retrieve assemblies
    use __ = Fake.Profile.startCategory Fake.Profile.Category.PaketGetAssemblies
    if logLevel.PrintVerbose then Trace.log <| sprintf "Retrieving the assemblies (rid: '%O')..." rid

    orderedGroup
    |> Seq.filter (fun p ->
      if p.Name.ToString() = "Microsoft.FSharp.Core.netcore" then
        eprintfn "Ignoring 'Microsoft.FSharp.Core.netcore' please tell the package authors to fix their package and reference 'FSharp.Core' instead."
        false
      else true)
    |> Seq.map (fun p -> async {
      match cache.InstallModelTask groupName p.Name with
      | None -> return failwith "InstallModel not cached?"
      | Some installModelTask ->
        let! orig = installModelTask |> Async.AwaitTask
        let installModel =
          orig
            .ApplyFrameworkRestrictions(Paket.Requirements.getExplicitRestriction p.Settings.FrameworkRestrictions)
        let targetProfile = Paket.TargetProfile.SinglePlatform framework
  
        let refAssemblies =
          installModel.GetCompileReferences targetProfile
          |> Seq.map (fun fi -> true, FileInfo fi.Path)
        let! graph = graph |> Async.AwaitTask
        let runtimeAssemblies =
          installModel.GetRuntimeAssemblies graph rid targetProfile
          |> Seq.map (fun fi -> false, FileInfo fi.Library.Path)
          |> Seq.toList
        let runtimeLibraries =
          installModel.GetRuntimeLibraries graph rid targetProfile
          |> Seq.map (fun fi -> DependencyFile.Library { File = fi.Library.Path })
          |> Seq.toList
        let result =
          Seq.append runtimeAssemblies refAssemblies
          |> Seq.filter (fun (_, r) -> r.Extension = ".dll" || r.Extension = ".exe" )
          |> Seq.map (fun (isRef, fi) -> false, isRef, fi)
          |> Seq.choose (filterValidAssembly logLevel)
          |> Seq.map DependencyFile.Assembly
          |> Seq.append runtimeLibraries
          |> Seq.toList
        return result })
    |> Async.Parallel
    |> fun asy ->
        let work = asy |> Async.StartAsTask
#if DOTNETCORE
        let sdkRefs =
            (getCurrentSDKReferenceFiles()
                 |> Seq.map (fun file -> true, true, FileInfo file)
                 |> Seq.choose (filterValidAssembly logLevel))
                 |> Seq.map DependencyFile.Assembly
#endif  
        work.Result
        |> Seq.collect id
#if DOTNETCORE
        // Append sdk files as references in order to properly compile, for runtime we can default to the default-load-context.
        |> Seq.append sdkRefs
#endif  
    // If we have multiple select one
    |> Seq.groupBy (function
        | DependencyFile.Assembly ass -> ass.IsReferenceAssembly, System.Reflection.AssemblyName(ass.Info.FullName).Name
        | DependencyFile.Library lib -> false, lib.File)
    |> Seq.map (fun (_, group) -> group |> Seq.maxBy (function
        | DependencyFile.Assembly ass -> Version.Parse ass.Info.Version
        | DependencyFile.Library lib -> new Version()))
    |> Seq.toList

  let restoreOrUpdate () =
    if logLevel.PrintVerbose then Trace.log "Restoring with paket..."

    // Update
    let localLock = script + ".lock" // the primary lockfile-path </> lockFilePath.FullName is implementation detail
    let needLocalLock = lockFilePath.FullName.Contains (Path.GetFullPath cacheDir) // Only primary if not external already.
    let localLockText = lazy File.ReadAllText localLock
    if needLocalLock && File.Exists localLock && (not (File.Exists lockFilePath.FullName) || localLockText.Value <> File.ReadAllText lockFilePath.FullName) then
      File.Copy(localLock, lockFilePath.FullName, true)
    if needLocalLock && not (File.Exists localLock) then
      File.Delete lockFilePath.FullName
    if not <| File.Exists lockFilePath.FullName then
      if logLevel.PrintVerbose then Trace.log "Lockfile was not found. We will update the dependencies and write our own..."
      try
        paketApi.UpdateGroup(groupStr, false, false, false, false, false, Paket.SemVerUpdateMode.NoRestriction, false)
        |> ignore
      with
      | e when e.Message.Contains "Did you restore group" ->
        // See https://github.com/fsharp/FAKE/issues/1672
        // and https://github.com/fsprojects/Paket/issues/2785
        // We do a restore anyway.
        eprintfn "paket update has thrown an error: %O" e
        ()
      if needLocalLock then File.Copy(lockFilePath.FullName, localLock, true)
    
    // Restore
    if config.RestoreOnlyGroup then
        if config.UseSimpleRestore
        then RestoreProcess.Restore(paketApi.DependenciesFile,RestoreProcess.RestoreProjectOptions.NoProjects,false,Option.map Paket.Domain.GroupName group,true,false,None,None,true)
        else paketApi.Restore(false,group,[],false,false,false,None,None)
    else
        if config.UseSimpleRestore
        then paketApi.SimplePackagesRestore()
        else paketApi.Restore()
    
  // https://github.com/fsharp/FAKE/issues/1908
  writeIntellisenseFile cacheDir // write intellisense.fsx immediately
  let writeIntellisenseTask = // write intellisense_lazy.fsx when something changed.
    lazy
      writeIntellisenseLazyFile cacheDir {
        Cache = cache.Value
        ScriptType = Paket.LoadingScripts.ScriptGeneration.ScriptType.FSharp
        Groups = [groupName]
        DefaultFramework = false, (Paket.FrameworkIdentifier.DotNetFramework (Paket.FrameworkVersion.V4_7_1))
      }
      |> Async.StartAsTask

  let readFromCache () =
      File.ReadLines(dependencyCacheFile)
      |> Seq.map (fun line ->
        let splits = line.Split(';')
        let typ = splits.[0]
        let (|Library|Assembly|) (input:string) =
            match input.ToUpperInvariant() with
            | "LIBRARY" -> Library
            | "RUNTIMEASSEMBLY" -> Assembly false
            | "REFERENCEASSEMBLY" -> Assembly true
            | _ -> failwithf "Cache is invalid as '%s' is not a valid type" input
        match typ.ToUpperInvariant() with
        | Library ->
            let loc = Path.readPathFromCache config.ScriptFilePath splits.[1]
            if not (File.Exists loc) then
                failwithf "Cache is invalid as '%s' doesn't exist" loc
            DependencyFile.Library { File = loc }
        | Assembly isRef ->
            let ver = splits.[1]
            let loc = Path.readPathFromCache config.ScriptFilePath splits.[2]
            let fullName = splits.[3]
            if not (File.Exists loc) then
                failwithf "Cache is invalid as '%s' doesn't exist" fullName
            DependencyFile.Assembly
              { IsReferenceAssembly = isRef
                Info =
                  { Runners.AssemblyInfo.FullName = fullName
                    Runners.AssemblyInfo.Version = ver
                    Runners.AssemblyInfo.Location = loc } })
      |> Seq.toList
      
  let writeToCache (list:DependencyFile list) =
      list
      |> Seq.map (function
        | DependencyFile.Assembly item ->
            sprintf "%s;%s;%s;%s"
                (if item.IsReferenceAssembly then "REFERENCEASSEMBLY" else "RUNTIMEASSEMBLY")
                item.Info.Version
                (Path.fixPathForCache config.ScriptFilePath item.Info.Location)
                item.Info.FullName
        | DependencyFile.Library lib ->
            sprintf "%s;%s" "LIBRARY" (Path.fixPathForCache config.ScriptFilePath lib.File))
      |> fun lines -> File.WriteAllLines(dependencyCacheFile, lines)
      File.Copy(lockFilePath.FullName, dependencyCacheHashFile, true)
      ()
      
  let getKnownDependencies () =
      CoreCache.getCached
          (fun () -> let list = retrieveInfosUncached() in writeIntellisenseTask.Value |> ignore; list)
          readFromCache
          writeToCache
          (fun _ -> File.Exists dependencyCacheHashFile && File.Exists dependencyCacheFile && File.ReadAllText dependencyCacheHashFile = File.ReadAllText lockFilePath.FullName)
  
  // Restore or update immediatly, because or everything might be OK -> cached path.
  //let knownAssemblies, writeIntellisenseTask = restoreOrUpdate()
  do restoreOrUpdate()
  let knownDependencies = lazy getKnownDependencies()

  if logLevel.PrintVerbose then
    Trace.tracefn "Known dependencies: \n\t%s" (System.String.Join("\n\t", knownDependencies.Value |> Seq.map (fun d ->
        let typ, ver =
            match d with
            | DependencyFile.Assembly a -> (if a.IsReferenceAssembly then "ref" else "lib"), sprintf " (%s)" a.Info.Version
            | DependencyFile.Library _ -> "native", ""
        sprintf " - %s: %s%s" (typ) d.Location ver)))
  { new CoreCache.ICachingProvider with
      member x.CleanCache context =
        if logLevel.PrintVerbose then Trace.log "Invalidating cache..."
        let assemblyPath, warningsFile = context.CachedAssemblyFilePath + ".dll", context.CachedAssemblyFilePath + ".warnings"
        try File.Delete warningsFile; File.Delete assemblyPath
        with e -> Trace.traceError (sprintf "Failed to delete cached files: %O" e)
      member __.TryLoadCache (context) =
          let references =
              knownDependencies.Value
              |> List.choose (function DependencyFile.Assembly a when a.IsReferenceAssembly -> Some a | _ -> None)
              |> List.map (fun (a:AssemblyData) -> a.Info.Location)
          let runtimeAssemblies =
              knownDependencies.Value
              |> List.choose (function DependencyFile.Assembly a when not a.IsReferenceAssembly -> Some a | _ -> None)
              |> List.map (fun a -> a.Info)
          let nativeLibraries =
              knownDependencies.Value
              |> List.choose (function DependencyFile.Library a -> Some a | _ -> None)
          let newAdditionalArgs =
              { context.Config.CompileOptions.FsiOptions with
                  NoFramework = true
                  References = references @ context.Config.CompileOptions.FsiOptions.References
                  Debug = Some Yaaf.FSharp.Scripting.DebugMode.Portable }
          { context with
              Config =
                { context.Config with
                    CompileOptions = 
                      { context.Config.CompileOptions with 
                         FsiOptions = newAdditionalArgs}
                    RuntimeOptions =
                      { context.Config.RuntimeOptions with
                         _RuntimeDependencies = runtimeAssemblies @ context.Config.RuntimeOptions.RuntimeDependencies
                         _NativeLibraries = nativeLibraries @ context.Config.RuntimeOptions.NativeLibraries }

                }
          },
          let assemblyPath, warningsFile = context.CachedAssemblyFilePath + ".dll", context.CachedAssemblyFilePath + ".warnings"
          if File.Exists (assemblyPath) && File.Exists (warningsFile) then
              Some { CompiledAssembly = assemblyPath; Warnings = File.ReadAllText(warningsFile) }
          else None
      member x.SaveCache (context, cache) =
          if logLevel.PrintVerbose then Trace.log "saving cache..."
          File.WriteAllText (context.CachedAssemblyFilePath + ".warnings", cache.Warnings)
          if writeIntellisenseTask.IsValueCreated then 
            writeIntellisenseTask.Value.Wait() }

let internal restoreDependencies config cacheDir section =
  match section with
  | FakeHeader.PaketDependencies (_, paketDependencies, paketDependenciesFile, group) ->
    paketCachingProvider config cacheDir paketDependencies paketDependenciesFile group

let internal tryFindGroupFromDepsFile scriptDir =
    let depsFile = Path.Combine(scriptDir, "paket.dependencies")
    if File.Exists (depsFile) then
        match
            File.ReadAllLines(depsFile)
            |> Seq.map (fun l -> l.Trim())
            |> Seq.fold (fun (takeNext, result) l ->
                // find '// [ FAKE GROUP ]' and take the next one.
                match takeNext, result with
                | _, Some s -> takeNext, Some s
                | true, None ->
                    if not (l.ToLowerInvariant().StartsWith "group") then
                        Trace.traceFAKE "Expected a group after '// [ FAKE GROUP]' comment, but got %s" l
                        false, None
                    else
                        let splits = l.Split([|" "|], StringSplitOptions.RemoveEmptyEntries)
                        if splits.Length < 2 then
                            Trace.traceFAKE "Expected a group name after '// [ FAKE GROUP]' comment, but got %s" l
                            false, None
                        else
                            false, Some (splits.[1])
                | _ -> if l.Contains "// [ FAKE GROUP ]" then true, None else false, None) (false, None)
            |> snd with
        | Some group ->
            let fullpath = Path.GetFullPath depsFile
            FakeHeader.PaketDependencies (FakeHeader.PaketDependenciesRef, Dependencies fullpath, (lazy DependenciesFile.ReadFromFile fullpath), Some group)
            |> Some
        | _ -> None
    else None

type PreparedDependencyType =
  | PaketInline
  | PaketDependenciesRef
  | DefaultDependencies

type PrepareInfo =
  internal
    { _CacheDir : string
      _Config : FakeConfig
      _Section : FakeHeader.FakeSection
      _DependencyType : PreparedDependencyType }
  member x.CacheDir = x._CacheDir
  member x.DependencyType = x._DependencyType

type TryPrepareInfo =
    | Prepared of PrepareInfo
    | NoHeader of cacheDir:Lazy<string> * saveCache:(unit->unit)

/// Doesn't create the .fake folder for this file if we don't detect a fake script
let tryPrepareFakeScript (config:FakeConfig) : TryPrepareInfo =
    let script = config.ScriptFilePath
    let scriptDir = Path.GetDirectoryName (script)
    let cacheDirUnsafe = Path.Combine(scriptDir, ".fake", Path.GetFileName(script))
    let cacheDir =
        lazy
            Directory.CreateDirectory (cacheDirUnsafe)  |> ignore<DirectoryInfo>
            cacheDirUnsafe
    
    let mutable actions = []
    let scriptSectionHashFile = Path.Combine(cacheDirUnsafe, "fake-section.cached")
    let scriptSectionCacheFile = Path.Combine(cacheDirUnsafe, "fake-section.txt")
    let inline getSectionUncached () =
        use __ = Fake.Profile.startCategory Fake.Profile.Category.Analyzing
        let newSection = FakeHeader.tryReadPaketDependenciesFromScript config.ScriptTokens.Value cacheDir script
        match newSection with
        | Some s -> Some s
        | None ->
          tryFindGroupFromDepsFile scriptDir
    let writeToCache (section : FakeHeader.FakeSection option) =
        let content =
            match section with 
            | Some (FakeHeader.PaketDependencies(headerType, p, _, group)) ->
                let s =
                  match headerType with
                  | FakeHeader.PaketInline -> "paket-inline"
                  | FakeHeader.PaketDependenciesRef -> "paket-ref"
                sprintf "paket: %s, %s, %s" (Path.fixPathForCache config.ScriptFilePath p.DependenciesFile) (match group with | Some g -> g | _ -> "<null>") s
            | None -> "none"
        actions <- (fun () ->
            ignore cacheDir.Value // init cache
            File.WriteAllText(scriptSectionCacheFile, content)
            File.Copy (script, scriptSectionHashFile, true)) :: actions
        
        
    let readFromCache () =
        let t = File.ReadAllText(scriptSectionCacheFile).Trim()
        if t.StartsWith("paket:") then 
            let s = t.Substring("paket: ".Length)
            let splits = s.Split(',')
            if splits.Length < 3 then
              raise <| CoreCache.CacheOutdated
            else
              let depsFile = Path.readPathFromCache config.ScriptFilePath splits.[0]
              // An old version of the template created the cache
              if not (File.Exists depsFile) then
                  raise <| CoreCache.CacheOutdated
              let group =
                  let trimmed = splits.[1].Trim()
                  if trimmed = "<null>" then None else Some trimmed
              let headerType =
                match splits.[2].Trim() with
                | "paket-inline" -> FakeHeader.PaketInline
                | "paket-ref" -> FakeHeader.PaketDependenciesRef
                | s -> failwithf "Invalid header type '%s'" s         
              let fullpath = Path.GetFullPath depsFile
              FakeHeader.PaketDependencies (headerType, Dependencies fullpath, (lazy DependenciesFile.ReadFromFile fullpath), group) |> Some
        else None
        
    let section =
        CoreCache.getCached
            getSectionUncached
            readFromCache
            writeToCache
            (fun _ -> File.Exists scriptSectionHashFile && File.Exists scriptSectionCacheFile && File.ReadAllText scriptSectionHashFile = File.ReadAllText script)

    match section with
    | Some section ->
        actions |> List.rev |> List.iter (fun f -> f())
        { _CacheDir = cacheDir.Value
          _Config = config
          _Section = section
          _DependencyType =
            match section with
            | FakeHeader.PaketDependencies(FakeHeader.PaketInline, _, _, _) -> PreparedDependencyType.PaketInline
            | FakeHeader.PaketDependencies(FakeHeader.PaketDependenciesRef, _, _, _) -> PreparedDependencyType.PaketDependenciesRef }
        |> Prepared      
    | None ->
        NoHeader(cacheDir, (fun () -> actions |> List.rev |> List.iter (fun f -> f())))


let prepareFakeScript (config:FakeConfig) : PrepareInfo =
    match tryPrepareFakeScript config with
    | Prepared s -> s
    | NoHeader(cacheDir, saveCache) ->
        saveCache()

        let defaultPaketCode = """
        source https://api.nuget.org/v3/index.json
        storage: none
        framework: netstandard2.0
        nuget FSharp.Core
                """
        if Environment.environVar "FAKE_ALLOW_NO_DEPENDENCIES" <> "true" then
            Trace.traceFAKE """Consider adding your dependencies via `#r` dependencies, for example add '#r "paket: nuget FSharp.Core //"'.
See https://fake.build/fake-fake5-modules.html for details. 
If you know what you are doing you can silence this warning by setting the environment variable 'FAKE_ALLOW_NO_DEPENDENCIES' to 'true'"""
        let section =
            { FakeHeader.Header = FakeHeader.PaketInline
              FakeHeader.Section = defaultPaketCode }
            |> FakeHeader.writeFixedPaketDependencies cacheDir
        { _CacheDir = cacheDir.Value
          _Config = config
          _Section = section
          _DependencyType = PreparedDependencyType.DefaultDependencies }

let restoreAndCreateCachingProvider (p:PrepareInfo) =
    restoreDependencies p._Config p._CacheDir p._Section

let createConfig (logLevel:Trace.VerboseLevel) (fsiOptions:string list) scriptPath scriptArgs onErrMsg onOutMsg useCache restoreOnlyGroup =
  // fixes https://github.com/fsharp/FAKE/issues/2314
  let scriptPath = Path.normalizeFileName scriptPath
  if logLevel.PrintVerbose then Trace.log (sprintf "prepareAndRunScriptRedirect(Script: %s, fsiOptions: %A)" scriptPath (System.String.Join(" ", fsiOptions)))
  let fsiOptionsObj = Yaaf.FSharp.Scripting.FsiOptions.ofArgs fsiOptions
  let newFsiOptions =
    { fsiOptionsObj with
#if !NETSTANDARD1_6
        Defines = "FAKE" :: fsiOptionsObj.Defines
#else
        Defines = "DOTNETCORE" :: "FAKE" :: fsiOptionsObj.Defines
#endif
      }
  use out = Yaaf.FSharp.Scripting.ScriptHost.CreateForwardWriter onOutMsg
  use err = Yaaf.FSharp.Scripting.ScriptHost.CreateForwardWriter onErrMsg
  let tokenized = lazy (File.ReadLines scriptPath |> FSharpParser.getTokenized scriptPath ("FAKE_DEPENDENCIES" :: newFsiOptions.Defines))

  { Runners.FakeConfig.VerboseLevel = logLevel
    Runners.FakeConfig.ScriptFilePath = Path.GetFullPath scriptPath
    Runners.FakeConfig.ScriptTokens = tokenized
    Runners.FakeConfig.CompileOptions =
      { FsiOptions = newFsiOptions }
    Runners.FakeConfig.RuntimeOptions =
      { _RuntimeDependencies = []; _NativeLibraries = [] }
    Runners.FakeConfig.UseSimpleRestore = false
    Runners.FakeConfig.UseCache = useCache
    Runners.FakeConfig.RestoreOnlyGroup = restoreOnlyGroup
    Runners.FakeConfig.Out = out
    Runners.FakeConfig.Err = err
    Runners.FakeConfig.ScriptArgs = scriptArgs }

let createConfigSimple (logLevel:Trace.VerboseLevel) (fsiOptions:string list) scriptPath scriptArgs useCache restoreOnlyGroup =
    createConfig logLevel fsiOptions scriptPath scriptArgs (printf "%s") (printf "%s") useCache restoreOnlyGroup

let runScript (preparedScript:PrepareInfo) : RunResult * ResultCoreCacheInfo * FakeContext =
    let cachingProvider = restoreAndCreateCachingProvider preparedScript
    CoreCache.runScriptWithCacheProviderExt preparedScript._Config cachingProvider
