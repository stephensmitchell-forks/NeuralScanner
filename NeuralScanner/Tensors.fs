﻿namespace NeuralScanner

open System
open System.Runtime.InteropServices
open System.IO
open System.Numerics
open System.Globalization

open MetalTensors

type SdfFrame (depthPath : string, dataDirectory : string) =
    let samplingDistance = 1.0e-3f

    let width, height, depths =
        let f = File.OpenRead (depthPath)
        let r = new BinaryReader (f)
        let magic = r.ReadInt32 ()
        let width = r.ReadInt32 ()
        let height = r.ReadInt32 ()
        let stride = r.ReadInt32 ()
        let dataSize = r.ReadInt32 ()
        let pixelFormat = r.ReadInt32 ()
        let len = width * height
        assert(len = dataSize/4)
        let depths : float32[] = Array.zeroCreate len
        let span = MemoryMarshal.AsBytes(depths.AsSpan())
        let n = f.Read (span)
        assert(n = dataSize)
        r.Close()
        width, height, depths

    let confidences =
        let path = depthPath.Replace("_Depth", "_DepthConfidence")
        let f = File.OpenRead (path)
        let r = new BinaryReader (f)
        let magic = r.ReadInt32 ()
        let width = r.ReadInt32 ()
        let height = r.ReadInt32 ()
        let stride = r.ReadInt32 ()
        let dataSize = r.ReadInt32 ()
        let pixelFormat = r.ReadInt32 ()
        let len = width * height
        assert(len = dataSize)
        let confs : byte[] = Array.zeroCreate len
        let span = MemoryMarshal.AsBytes(confs.AsSpan())
        let n = f.Read (span)
        assert(n = dataSize)
        r.Close()
        confs

    let pointCount = depths.Length

    let loadMatrix (path : string) =
        let rows =
            File.ReadAllLines(path)
            |> Array.map (fun x ->
                x.Split(' ')
                |> Seq.skip 1
                |> Seq.map (fun y -> Single.Parse (y, CultureInfo.InvariantCulture))
                |> Array.ofSeq)                
        Matrix4x4(rows.[0].[0], rows.[0].[1], rows.[0].[2], rows.[0].[3],
                  rows.[1].[0], rows.[1].[1], rows.[1].[2], rows.[1].[3],
                  rows.[2].[0], rows.[2].[1], rows.[2].[2], rows.[2].[3],
                  rows.[3].[0], rows.[3].[1], rows.[3].[2], rows.[3].[3])

    let resolution =
        let text = File.ReadAllText (depthPath.Replace ("_Depth.pixelbuffer", "_Resolution.txt"))
        let parts = text.Trim().Split(' ')
        (Single.Parse (parts.[0], CultureInfo.InvariantCulture), Single.Parse (parts.[1], CultureInfo.InvariantCulture))

    let intrinsics =
        let mutable m = loadMatrix (depthPath.Replace ("_Depth.pixelbuffer", "_Intrinsics.txt"))
        let colorWidth, _ = resolution
        let iscale = float32 width / float32 colorWidth
        m.M11 <- m.M11 * iscale
        m.M22 <- m.M22 * iscale
        m.M13 <- m.M13 * iscale
        m.M23 <- m.M23 * iscale
        m
    let projection = loadMatrix (depthPath.Replace ("_Depth.pixelbuffer", "_Projection.txt"))
    let transform =
        let m = loadMatrix (depthPath.Replace ("_Depth.pixelbuffer", "_Transform.txt"))
        Matrix4x4.Transpose(m)

    let index x y = y * width + x

    let cameraPosition (x : int) (y : int) depthOffset : Vector4 =
        let depth = -(depths.[index x y] + depthOffset)
        let xc = -(float32 x - intrinsics.M13) * depth / intrinsics.M11
        let yc = (float32 y - intrinsics.M23) * depth / intrinsics.M22
        Vector4(xc, yc, depth, 1.0f)

    let worldPosition (x : int) (y : int) depthOffset : Vector4 =
        let camPos = cameraPosition x y depthOffset
        // World = Transform * Camera
        // World = Camera * Transform'
        let testResult = Vector4.Transform(Vector4.UnitW, transform)
        Vector4.Transform(camPos, transform)

    let vector3Shape = [| 3 |]
    let freespaceShape = [| 1 |]
    let distanceShape = [| 1 |]

    member this.PointCount = pointCount

    member this.GetRow (random : Random, inside: bool) : struct (Tensor[]*Tensor[]) =
        // Find a medium to high confidence pixel to sample
        let mutable x = random.Next(width)
        let mutable y = random.Next(height)
        let maxTries = 100
        let mutable t = 0
        while t < maxTries && confidences.[index x y] <= 0uy do
            x <- random.Next(width)
            y <- random.Next(height)
            t <- t + 1

        // Half the time inside, half outside
        if inside then
            let depthOffset = float32 (random.NextDouble()) * samplingDistance
            let pos = worldPosition x y depthOffset
            let inputs = [| Tensor.Array(vector3Shape, pos.X, pos.Y, pos.Z)
                            Tensor.Array(freespaceShape, 0.0f)
                            Tensor.Array(distanceShape, -depthOffset) |]
            struct (inputs, [| |])
        else
            // Outside
            if random.Next(2) = 0 then
                // Surface
                let depthOffset = -float32 (random.NextDouble()) * samplingDistance
                let pos = worldPosition x y depthOffset
                let inputs = [| Tensor.Array(vector3Shape, pos.X, pos.Y, pos.Z)
                                Tensor.Array(freespaceShape, 0.0f)
                                Tensor.Array(distanceShape, -depthOffset) |]
                struct (inputs, [| |])
            else
                // Freespace
                let depthOffset = -float32 (random.NextDouble()) * depths.[index x y]
                let pos = worldPosition x y depthOffset
                let inputs = [| Tensor.Array(vector3Shape, pos.X, pos.Y, pos.Z)
                                Tensor.Array(freespaceShape, 1.0f)
                                Tensor.Array(distanceShape, -depthOffset) |]
                struct (inputs, [| |])

type SdfDataSet (dataDirectory : string) =
    inherit DataSet ()

    let depthFiles = Directory.GetFiles (dataDirectory, "*_Depth.pixelbuffer")

    let frames =
        depthFiles
        |> Array.map (fun x -> SdfFrame (x, dataDirectory))
    let count = depthFiles |> Seq.sumBy (fun x -> 1)
    do if count = 0 then failwithf "No files in %s" dataDirectory

    let random = new System.Random ()

    let mutable nextIsInside = false

    override this.Count = frames |> Array.sumBy (fun x -> x.PointCount)

    override this.GetRow (_, _) =
        let fi = random.Next(frames.Length)
        let r = frames.[fi].GetRow (random, nextIsInside)
        nextIsInside <- not nextIsInside
        r


type Trainer () =

    let networkDepth = 8
    let networkWidth = 512
    let dropoutRate = 0.2f

    let learningRate = 1.0e-5f

    let batchTrained = Event<_> ()

    let volumeMin = Vector3 (-0.30533359f, -1.12338264f, -0.89218203f)
    let volumeMax = Vector3 (0.69466641f, -0.62338264f, 0.10781797f)

    let createSdfModel () =
        let input = Tensor.Input("xyz", 3)
        let hiddenLayer (x : Tensor) =
            x.Dense(networkWidth).ReLU().Dropout(dropoutRate)
        let houtput1 =
            (seq{1..(networkDepth/2)}
             |> Seq.fold (fun a _ -> hiddenLayer a) input)
        let inner = input.Dense(networkWidth).ReLU().Concat(houtput1)
        let houtput =
            (seq{1..(networkDepth/2)}
             |> Seq.fold (fun a _ -> hiddenLayer a) inner)
        let output = houtput.Dense(networkWidth).ReLU().Dense(1).Tanh()
        let model = Model (input, output, "SDF")
        //let r = model.Compile (Loss.MeanAbsoluteError,
        //                       new AdamOptimizer(learningRate))
        model

    let lossClipDelta = 1.0e-2f

    let createTrainingModel (sdfModel : Model) : Model =
        let inputXyz = Tensor.Input("xyz", 3)
        let inputFreespace = Tensor.Input("freespace", 1)
        let inputExpected = Tensor.Input("distance", 1)
        let output = sdfModel.Call(inputXyz)
        let model = Model ([|inputXyz; inputFreespace; inputExpected|], [|output|], "TrainSDF")

        let clipOutput = output.Clip(-lossClipDelta, lossClipDelta)
        let clipExpected = inputExpected.Clip(-lossClipDelta, lossClipDelta)
        let surfaceLoss = (clipOutput - clipExpected).Abs().SpatialMean()

        let freespaceLoss = (0.0f - output).Clip(0.0f, lossClipDelta).SpatialMean()

        let totalLoss =
            surfaceLoss * (1.0f - inputFreespace) +
            freespaceLoss * inputFreespace

        model.AddLoss (totalLoss)
        let r = model.Compile (new AdamOptimizer(learningRate))
        model

    let dataDir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)

    let batchSize = 1024

    let modelPath = dataDir + "/Onewheel.zip"

    let model = 
        if false && File.Exists modelPath then
            let fileSize = (new FileInfo(modelPath)).Length
            let model = Model.Load (modelPath)
            let r = model.Compile (Loss.MeanAbsoluteError,
                                    new AdamOptimizer(learningRate))
            model
        else
            createSdfModel ()

    let trainingModel = createTrainingModel model

    member this.BatchTrained = batchTrained.Publish

    member this.Train () =
        //let data = SdfDataSet ("/Users/fak/Data/NeuralScanner/Onewheel")
        let data = SdfDataSet (dataDir)
        let struct(inputs, outputs) = data.GetRow(0, null)
        printfn "%O" trainingModel
        let mutable totalTrained = 0
        let epochs = 1.0f
        let callback (h : TrainingHistory.BatchHistory) =
            //printfn "LOSS %g" h.AverageLoss
            totalTrained <- batchSize + totalTrained
            let progress = float32 totalTrained / (epochs * float32 data.Count)
            batchTrained.Trigger (progress, totalTrained, h.AverageLoss)
            ()
        let history = trainingModel.Fit(data, batchSize = batchSize, epochs = epochs, callback = fun h -> callback h)
        this.GenerateMesh ()
        ()

    member this.Save () =
        trainingModel.Save (modelPath)
        ()

    member this.GenerateMesh () =

        let nx, ny, nz = 64, 64, 64
        let mutable numPoints = 0
        let totalPoints = nx*ny*nz

        let sdf (x : Memory<Vector3>) (y : Memory<Vector4>) =
            let batchTensors = Array.init x.Length (fun i ->
                let x = x.Span
                let input = Tensor.Array(x.[i].X, x.[i].Y, x.[i].Z)
                [|input|])
            let results = model.Predict(batchTensors)
            let y = y.Span
            for i in 0..(x.Length-1) do
                let r = results.[i].[0]
                let yvec = Vector4(1.0f, 1.0f, 1.0f, r.[0])
                y.[i] <- yvec
            numPoints <- numPoints + y.Length
            let progress = float numPoints / float totalPoints
            printfn "NN Sample Progress: %.1f" (progress * 100.0)

        let voxels = SdfKit.Voxels.SampleSdf (sdf, volumeMin, volumeMax, nx, ny, nz, batchSize = batchSize, maxDegreeOfParallelism = 2)
        let mesh = SdfKit.MarchingCubes.CreateMesh (voxels, 0.0f, step = 1)
        mesh.WriteObj (dataDir + "/OnewheelBetter.obj")
        ()







