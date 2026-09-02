namespace Lux.Ba

open CeresSharp
open Aardvark.Base
open System.Threading

/// Faithful reimplementation of Lumen's LightBA (recovered from cp.dll: ceres::AutoDiffCostFunction
/// <CameraProjection|IntrinsicsCost|EntrancePupilCost, LightBA> + CauchyLoss). A Ceres bundle adjustment
/// that refines a per-capture camera (the mirror-driven tele virtual camera whose calibrated pose/intrinsics
/// drift) from 2D–3D correspondences: it co-optimizes pose + focal + radial distortion — more than a plain
/// PnP, which fixes intrinsics. Built on CeresSharp (native Google Ceres, Win/Mac/Linux) with automatic
/// differentiation, mirroring Lumen's AutoDiffCostFunction. Callable from C# (Lux.Engine).
module LightBa =

    let mutable private inited = 0
    let private ensureInit () =
        if Interlocked.CompareExchange(&inited, 1, 0) = 0 then Aardvark.Init()

    /// CameraProjection residual: reproject 3D point via Rodrigues(rvec)·X + t → perspective → radial
    /// distortion (k1,k2) → K, minus the observation. Autodiff over the parameter block.
    let private reproject (fx:float) (fy:float) (cx:float) (cy:float) (px:float) (py:float) (pz:float)
                          (rx:scalar) (ry:scalar) (rz:scalar) (tx:scalar) (ty:scalar) (tz:scalar)
                          (sf:scalar) (k1:scalar) (k2:scalar) : scalar * scalar =
        let one = scalar 1.0
        let th = sqrt (rx*rx + ry*ry + rz*rz + scalar 1e-18)
        let ax, ay, az = rx/th, ry/th, rz/th
        let ct, st = cos th, sin th
        let sx, sy, sz = scalar px, scalar py, scalar pz
        let dax = ay*sz - az*sy
        let day = az*sx - ax*sz
        let daz = ax*sy - ay*sx
        let adot = ax*sx + ay*sy + az*sz
        let Xc = sx*ct + dax*st + ax*adot*(one - ct) + tx
        let Yc = sy*ct + day*st + ay*adot*(one - ct) + ty
        let Zc = sz*ct + daz*st + az*adot*(one - ct) + tz
        let xn, yn = Xc/Zc, Yc/Zc
        let r2 = xn*xn + yn*yn
        let rad = one + k1*r2 + k2*r2*r2
        (scalar fx)*sf*(xn*rad) + scalar cx, (scalar fy)*sf*(yn*rad) + scalar cy

    /// Refine a camera from 3D–2D correspondences. pointsXyz: 3N floats (reference-frame points), obsUv: 2N
    /// floats (observed pixels), K = (fx,fy,cx,cy), rvec0/tvec0: initial angle-axis + translation (3 each).
    /// intrinsicsWeight > 0 adds the IntrinsicsCost prior pulling focalScale toward 1 (stay near calibration).
    /// Returns the 9 refined params [rx,ry,rz, tx,ty,tz, focalScale, k1, k2]. Robust (Cauchy) loss.
    let RefineCamera (pointsXyz: float[]) (obsUv: float[]) (fx:float) (fy:float) (cx:float) (cy:float)
                     (rvec0: float[]) (tvec0: float[]) (cauchyScalePx: float) (intrinsicsWeight: float) : float[] =
        ensureInit ()
        let n = pointsXyz.Length / 3
        use problem = new Problem()
        let init = [| rvec0.[0]; rvec0.[1]; rvec0.[2]; tvec0.[0]; tvec0.[1]; tvec0.[2]; 1.0; 0.0; 0.0 |]
        use pb = problem.AddParameterBlock init
        // CameraProjection reprojection residuals (2 per observation), Cauchy-robust
        problem.AddCostFunctionScalar(n*2, pb, LossFunction.CauchyLoss cauchyScalePx, fun (p: scalar[]) (res: scalar[]) ->
            for i in 0 .. n-1 do
                let u, v = reproject fx fy cx cy pointsXyz.[3*i] pointsXyz.[3*i+1] pointsXyz.[3*i+2]
                                     p.[0] p.[1] p.[2] p.[3] p.[4] p.[5] p.[6] p.[7] p.[8]
                let ou : float = obsUv.[2*i]
                let ov : float = obsUv.[2*i+1]
                res.[2*i]   <- u - scalar ou
                res.[2*i+1] <- v - scalar ov)
        // IntrinsicsCost prior: keep focalScale→1 and distortion k1,k2→0 near factory calibration (else the
        // free intrinsics/distortion overfit noisy A-depth points). Lumen's LightBA has this exact term.
        if intrinsicsWeight > 0.0 then
            let wI = scalar intrinsicsWeight
            problem.AddCostFunctionScalar(3, pb, LossFunction.TrivialLoss, fun (p: scalar[]) (res: scalar[]) ->
                res.[0] <- wI * (p.[6] - scalar 1.0)
                res.[1] <- wI * p.[7]
                res.[2] <- wI * p.[8])
        problem.Solve { maxIterations = 200; solverType = SolverType.DenseQr; print = false
                        functionTolerance = 1e-14; gradientTolerance = 1e-14; parameterTolerance = 1e-14 } |> ignore
        pb.Result
