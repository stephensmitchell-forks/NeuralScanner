﻿namespace NeuralScanner

open System
open System.IO
open System.Numerics
open SceneKit
open MetalTensors

module ProjectDefaults =
    let learningRate = 5.0e-4f
    let resolution = 32.0f
    let clipScale = 0.5f //?nani

    // Hyperparameters
    let outputScale = 200.0f
    let samplingDistance = 1.0e-3f
    let lossClipDelta = 1.0e-2f * outputScale
    let networkDepth = 8
    let networkWidth = 256
    let batchSize = 2*1024
    let numPositionEncodings = 4

    let useTanh = false
    let useFrameIndex = true

    // Derived
    let outsideDistance = lossClipDelta
    let outsideSdf = Vector4 (1.0f, 1.0f, 1.0f, outsideDistance)


type Project (settings : ProjectSettings, projectDir : string) =
    
    let settingsPath = Path.Combine (projectDir, "Settings.xml")
    let saveMonitor = obj ()

    let mutable depthPaths = Directory.GetFiles (projectDir, "*_Depth.pixelbuffer")
    let mutable imagePaths = Directory.GetFiles (projectDir, "*_Image.jpg")

    let frames = System.Collections.Concurrent.ConcurrentDictionary<string, SdfFrame> ()

    let changed = Event<string> ()

    member this.ModifiedUtc = settings.ModifiedUtc

    member this.Changed = changed.Publish

    member this.ProjectDirectory = projectDir
    member this.CaptureDirectory = projectDir
    member this.DepthPaths = depthPaths
    member this.ImagePaths = imagePaths

    member this.Settings = settings

    member this.Name with get () : string = this.Settings.Name
                     and set v = this.Settings.Name <- v; this.SetModified "Name"
    member this.ExportFileName =
        let n = this.Name.Trim ()
        if n.Length > 0 then n.Replace("*", "_").Replace("?", "_").Replace("!", "_").Replace("/", "_")
        else "Untitled"

    member this.NumCaptures = imagePaths.Length
    member this.NewFrameIndex = this.NumCaptures

    member this.SaveSolidMesh (mesh : SdfKit.Mesh, meshId : string) : string =
        let path = Path.Combine (projectDir, sprintf "%s_SolidMesh_%s.obj" this.ExportFileName meshId)
        let tpath = Path.GetTempFileName ()
        mesh.WriteObj (tpath)
        if File.Exists path then
            File.Delete path
        File.Move (tpath, path)
        path

    member this.SaveSolidMeshAsUsdz (mesh : SdfKit.Mesh, meshId : string) : string =
        let upath = Path.Combine (projectDir, sprintf "%s_SolidMesh_%s.usdc" this.ExportFileName meshId)
        let zpath = Path.ChangeExtension (upath, ".usdz")
        let uurl = Foundation.NSUrl.FromFilename upath

        let goodTris = ResizeArray<int> ()
        for ti in 0..(mesh.Triangles.Length/3 - 1) do
            let a = mesh.Triangles.[3*ti+0]
            let b = mesh.Triangles.[3*ti+1]
            let c = mesh.Triangles.[3*ti+2]
            if a = b || b = c || a = c then
                () // Repeat
                printfn "REPEAT"
            else
                let ba = mesh.Vertices.[b] - mesh.Vertices.[a]
                let ca = mesh.Vertices.[c] - mesh.Vertices.[a]
                if ba.Length () < 1e-5f || ca.Length () < 1e-5f then
                    () // Bad Side
                    printfn "BAD SIDE: %O %O" ba ca
                else
                    goodTris.Add a
                    goodTris.Add b
                    goodTris.Add c
        printfn "TRIS NEW: %d OLD: %d" goodTris.Count mesh.Triangles.Length
        let mesh = SdfKit.Mesh(mesh.Vertices |> Array.copy, mesh.Colors, mesh.Normals |> Array.copy, goodTris.ToArray ())
        let mmin = mesh.Min
        let mmax = mesh.Max
        let mcenter = (mmin + mmax) * 0.5f
        let scale = 100.0f
        let transform =
            Matrix4x4.CreateTranslation(-mcenter.X, -mmin.Y, -mcenter.Z) *
            Matrix4x4.CreateScale(scale, scale, scale)
        mesh.Transform transform
        let smin = mesh.Min
        let smax = mesh.Max

        let asset = new ModelIO.MDLAsset ()
        asset.UpAxis <- OpenTK.NVector3(0.0f, 1.0f, 0.0f)
        let node = SceneKitGeometry.createSolidMeshNode mesh
        let mmesh = ModelIO.MDLMesh.FromGeometry(node.Geometry)
        //mmesh.AddAttribute(string ModelIO.MDLVertexAttributes.TextureCoordinate, ModelIO.MDLVertexFormat.Float2)
        //mmesh.AddUnwrappedTextureCoordinates (string ModelIO.MDLVertexAttributes.TextureCoordinate)
        mmesh.Name <- this.Name
        asset.AddObject mmesh
        let bb = asset.BoundingBox
        match asset.ExportAssetToUrl(uurl) with
        | false, e -> raise (new Foundation.NSErrorException (e))
        | true, _ ->
            if IO.File.Exists(zpath) then IO.File.Delete(zpath)
            do
                let ff = ICSharpCode.SharpZipLib.Zip.ZipFile.Create(zpath)
                let ename =
                    let n = Path.GetFileNameWithoutExtension(upath)
                    let n = if n.Length > 8 then n.Substring(0, 8) else n
                    n + ".usdc"
                ff.BeginUpdate ()
                let entry =  ff.EntryFactory.MakeFileEntry(upath, ename, true)
                entry.CompressionMethod <- ICSharpCode.SharpZipLib.Zip.CompressionMethod.Stored
                let ds = ICSharpCode.SharpZipLib.Zip.StaticDiskDataSource(upath)
                ff.Add (ds, entry)
                ff.CommitUpdate()
                ff.Close ()
            zpath
    member this.ClipTransform =
        let st = SCNMatrix4.Scale (this.Settings.ClipScale.X, this.Settings.ClipScale.Y, this.Settings.ClipScale.Z)
        let tt = SCNMatrix4.CreateTranslation (this.Settings.ClipTranslation.X, this.Settings.ClipTranslation.Y, this.Settings.ClipTranslation.Z)
        let rt = SCNMatrix4.CreateRotationY (this.Settings.ClipRotationDegrees.Y * (MathF.PI / 180.0f))
        st * rt * tt

    override this.ToString () = sprintf "Project %s" this.Name

    member this.SetModified (property : string) =
        this.Settings.ModifiedUtc <- DateTime.UtcNow
        this.Save ()
        this.SetChanged property

    member this.SetChanged (property : string) =
        changed.Trigger property

    member this.GetFrameWithDepthPath (depthPath : string) : SdfFrame =
        frames.GetOrAdd (depthPath, fun x -> SdfFrame x)

    member this.GetFrames () : SdfFrame[] =
        this.DepthPaths
        |> Array.map this.GetFrameWithDepthPath
        |> Array.sortBy (fun x -> x.FrameIndex)

    member this.GetVisibleFrames () : SdfFrame[] =
        this.DepthPaths
        |> Array.map this.GetFrameWithDepthPath
        |> Array.filter (fun x -> x.Visible)
        |> Array.sortBy (fun x -> x.FrameIndex)

    member this.AddFrame (frame : SdfFrame) =
        depthPaths <- Array.append depthPaths [| frame.DepthPath |]
        imagePaths <- Array.append imagePaths [| frame.ImagePath |]
        frames.[frame.DepthPath] <- frame
        changed.Trigger "NumCaptures"

    member this.UpdateCaptures () =
        depthPaths <- Directory.GetFiles (projectDir, "*_Depth.pixelbuffer")
        imagePaths <- Directory.GetFiles (projectDir, "*_Image.jpg")
        changed.Trigger "NumCaptures"

    member this.Save () =
        let config = this.Settings.Config
        Threading.ThreadPool.QueueUserWorkItem (fun _ ->
            lock saveMonitor (fun () ->
                config.Write (settingsPath)
                printfn "SAVED PROJECT: %s" settingsPath
                //let fileOutput = IO.File.ReadAllText(settingsPath)
                //printfn "FILE:\n%s" fileOutput
                //let newConfig = Config.Read<ProjectSettings> (settingsPath)
                ()))
        |> ignore

    member this.AutoSetBounds () =
        let frames = this.GetFrames ()
        if frames.Length > 0 then
            let mutable sum = Vector3.Zero
            let mutable num = 0
            let mutable fmin = frames.[0].MinPoint
            let mutable fmax = frames.[0].MaxPoint
            for f in frames do
                sum <- sum + f.CenterPoint
                num <- num + 1
                fmin <- Vector3.Min (fmin, f.MinPoint)
                fmax <- Vector3.Max (fmax, f.MaxPoint)
            let center =
                if num > 0 then sum / float32 num
                else sum
            let r = Vector3.Min(fmax - center, center - fmin)
            let bmin = Vector3.Max(center - r, fmin)
            let bmax = Vector3.Min(center + r, fmax)
            let bcenter = (bmax + bmin) * 0.5f
            let br = bmax - bcenter
            this.Settings.ClipTranslation <- bcenter
            this.Settings.ClipScale <- br
            this.SetModified "Settings.Clip"
            ()


and ProjectSettings (name : string,
                     learningRate : float32,
                     modifiedUtc : DateTime,
                     resolution : float32,
                     clipScale : Vector3,
                     clipRotationDegrees : Vector3,
                     clipTranslation : Vector3,
                     [<ConfigDefault (0)>] totalTrainedPoints : int,
                     [<ConfigDefault (0)>] totalTrainedSeconds : int
                    ) =
    inherit Configurable ()

    member val Name = name with get, set
    member val LearningRate = learningRate with get, set
    member val TotalTrainedPoints = totalTrainedPoints with get, set
    member val TotalTrainedSeconds = totalTrainedSeconds with get, set
    member val ModifiedUtc = modifiedUtc with get, set
    member val Resolution = resolution with get, set
    member val ClipScale : Vector3 = clipScale with get, set
    member val ClipRotationDegrees : Vector3 = clipRotationDegrees with get, set
    member val ClipTranslation : Vector3 = clipTranslation with get, set

    override this.Config =
        base.Config.Add("name", this.Name).Add("learningRate", this.LearningRate).Add("modifiedUtc", this.ModifiedUtc).Add("resolution", this.Resolution).Add("clipScale", this.ClipScale).Add("clipRotationDegrees", this.ClipRotationDegrees).Add("clipTranslation", this.ClipTranslation).Add("totalTrainedPoints", this.TotalTrainedPoints).Add("totalTrainedSeconds", this.TotalTrainedSeconds)


module ProjectManager =

    let projectsDir = Environment.GetFolderPath (Environment.SpecialFolder.MyDocuments)

    let mutable private loadedProjects : Map<string, Project> = Map.empty

    do Config.EnableReading<FrameConfig> ()
    do Config.EnableReading<ProjectSettings> ()

    let showError (e : exn) = printfn "ERROR: %O" e

    let loadProject (projectDir : string) : Project =
        match loadedProjects.TryFind projectDir with
        | Some x -> x
        | None ->
            let projectId = Path.GetFileName (projectDir)
            let settingsPath = Path.Combine (projectDir, "Settings.xml")
            let settings =
                try
                    //printfn "LOAD FROM: %s" settingsPath
                    Config.Read<ProjectSettings> (settingsPath)
                with ex ->
                    showError ex
                    let s = ProjectSettings ("Untitled",
                                             ProjectDefaults.learningRate,
                                             DateTime.UtcNow,
                                             ProjectDefaults.resolution,
                                             Vector3.One * ProjectDefaults.clipScale,
                                             Vector3.Zero,
                                             Vector3.Zero,
                                             totalTrainedPoints = 0,
                                             totalTrainedSeconds = 0
                                            )
                    try
                        s.Save (settingsPath)
                    with ex2 -> showError ex2
                    s
            let project = Project (settings, projectDir)
            project.UpdateCaptures ()
            loadedProjects <- loadedProjects.Add (projectDir, project)
            project

    let createNewProject () : unit =
        let newProjectId = Guid.NewGuid().ToString("D")
        let dirName = newProjectId
        let dirPath = Path.Combine(projectsDir, dirName)
        Directory.CreateDirectory(dirPath) |> ignore

    let findProjects () : Project[] =
        let projectDirs =
            IO.Directory.GetDirectories (projectsDir)
        projectDirs
        |> Array.map loadProject
        |> Array.sortBy (fun x -> x.Name)

