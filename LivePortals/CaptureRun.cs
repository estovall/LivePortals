using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using UnityEngine.Rendering;

namespace LivePortals
{
    /// <summary>
    /// One capture of a portal, spread out so the game keeps its frame rate. A few faces are rendered per frame and
    /// their pixels are read back asynchronously (no waiting for the GPU); everything per pixel (sky mask, depth,
    /// layering, encoding, saving) happens on a low-priority thread as each face's pixels arrive. Up to 0.9.3 all
    /// 21 faces were rendered and read back in one frame: a freeze of 1.3 s at every portal trip.
    /// </summary>
    internal class CaptureRun
    {
        private readonly TeleportWorld _portal;
        private readonly ZDOID _id;
        private Capture.Rig _rig;
        private Capture.Hidden _hidden;
        private Storage.Job _job;
        private GrassSet _grass;
        private FireSet _fire;
        private float _far, _exposure, _waterLevel;
        private readonly List<RawPoint> _points = new List<RawPoint>();
        private readonly List<FaceRaw> _faces = new List<FaceRaw>(); // in render order
        private readonly Queue<FaceRaw> _toProcess = new Queue<FaceRaw>();
        private readonly AutoResetEvent _signal = new AutoResetEvent(false);
        private volatile bool _renderDone, _abort;
        private int _issued, _collected;
        private Thread _worker;

        public volatile bool Done;
        public string Error;
        public float RenderMs;      // main-thread time spent rendering, all frames together
        public int RenderFrames;
        public int FrontFaces;
        public double WorkSeconds;
        public long TakenAt;
        public List<RawPoint> Points => _points;
        public int GrassCount => _grass != null ? _grass.Count : 0;
        public int FireCount => _fire != null ? _fire.Items.Count : 0;

        /// <summary>A departure: the place is about to unload, so render as fast as the frame allows (all faces at once if need be).</summary>
        internal bool Hurry;

        internal CaptureRun(TeleportWorld portal, ZDOID id)
        {
            _portal = portal; _id = id;
        }

        /// <summary>Main thread, one frame: cameras, viewpoints, the clearance checks, the grass record. Throws when the capture cannot start.</summary>
        internal void Prepare()
        {
            var shape = PortalShape.Of(_portal);
            Quaternion rot = _portal.transform.rotation;
            Vector3 centre = shape.Centre;
            var offsets = CaptureSet.PointOffsets(Plugin.CapturePoints.Value);
            // The secondary viewpoints spread with the opening: a stone portal's is about twice the wooden one's.
            float sx = Mathf.Clamp(shape.Width / 2.7f, 1f, 3f), sy = Mathf.Clamp(shape.Height / 2.8f, 1f, 3f);
            for (int i = 0; i < offsets.Length; i++) offsets[i] = new Vector3(offsets[i].x * sx, offsets[i].y * sy, offsets[i].z);

            _rig = Capture.MakeRig();
            if (_rig == null) throw new InvalidOperationException("no game camera");
            _far = _rig.Far; _exposure = Plugin.CaptureExposure.Value; _waterLevel = _rig.WaterLevel;
            _hidden = Capture.HideForCapture(_portal);
            try
            {
                if (Plugin.CaptureFlameDepth.Value) Capture.MakeProxies(_rig, _hidden);
                Physics.SyncTransforms(); // so the rays and clearance checks do not hit the colliders just switched off
                Vector3 forward = rot * Vector3.forward * 0.15f;
                Capture.EnsureProbed(_rig, centre + rot * offsets[0] + forward, rot);
                Capture.CheckAsync(_rig, centre + forward, rot);
                int clearMask = Capture.SolidMask(out _);
                for (int k = 0; k < offsets.Length; k++)
                {
                    if (k > 0 && !CaptureSet.FindClearance(centre + forward, rot, ref offsets[k], clearMask))
                    {
                        Plugin.Log.LogInfo($"LivePortals: viewpoint {offsets[k]} is inside or behind something here, skipped.");
                        continue;
                    }
                    var pt = new RawPoint { RingHeight = shape.Height, Offset = offsets[k], Res = _rig.Res, Step = _rig.Step, DepthRange = _rig.DepthRange, FaceTan = Capture.FaceTan };
                    Lighting.Sample(out pt.Sun, out pt.Ambient, out pt.Fog, out pt.DayFraction);
                    _points.Add(pt);
                    Vector3 pos = centre + rot * offsets[k] + forward;
                    for (int i = 0; i < 6; i++)
                    {
                        if (k > 0 && !CaptureSet.SecondaryFace(i)) continue;
                        _faces.Add(new FaceRaw { PointIndex = _points.Count - 1, Face = i, Pos = pos, Rot = rot * Capture.FaceRotations[i], NeedGpu = !_rig.Rays });
                    }
                }
                _grass = GrassSet.Record(centre, rot);
                if (_points.Count > 0 && _grass != null && _grass.Count > 0) _points[0].GrassGain = Capture.MeasureGrassGain(_rig, centre + forward, rot);
                if (Plugin.LiveFire.Value) _fire = FireSet.Record(centre, rot, _hidden.Fire);
            }
            finally { _hidden.Show(); }
            _job = Storage.Begin(_id);
            TakenAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            _worker = new Thread(Work) { IsBackground = true, Name = "LivePortals capture", Priority = System.Threading.ThreadPriority.BelowNormal };
            _worker.Start();
        }

        /// <summary>Main thread, over several frames: render the faces a few per frame, hand each one's pixels to the worker as they arrive.</summary>
        internal IEnumerator Render()
        {
            int perFrame = Hurry ? _faces.Count : Mathf.Max(1, Plugin.CaptureFacesPerFrame.Value);
            while (_issued < _faces.Count)
            {
                if (_portal == null || _abort) { Fail("the portal went away during the capture"); yield break; }
                float t0 = Time.realtimeSinceStartup;
                _hidden.Hide();
                try
                {
                    // At most perFrame faces, and no further face once this frame has spent its budget: a face is five
                    // or six camera renders, 25 ms on a middling machine, and two of them back to back is a frame
                    // at 20 fps, eleven times in a row.
                    float budget = Hurry ? 1f : Plugin.CaptureFrameBudgetMs.Value / 1000f;
                    for (int n = 0; n < perFrame && _issued < _faces.Count && (n == 0 || Time.realtimeSinceStartup - t0 < budget); n++, _issued++) Capture.RenderFace(_rig, _faces[_issued]);
                }
                catch (Exception e) { Fail("render failed: " + e.Message); }
                finally { _hidden.Show(); }
                if (Error != null) yield break;
                RenderMs += (Time.realtimeSinceStartup - t0) * 1000f;
                RenderFrames++;
                if (!Collect()) yield break;
                yield return null;
            }
            float deadline = Time.realtimeSinceStartup + 8f;
            while (_collected < _faces.Count)
            {
                if (!Collect()) yield break;
                if (_collected < _faces.Count)
                {
                    if (Time.realtimeSinceStartup > deadline) { Fail("the GPU did not hand the pixels back in time"); yield break; }
                    yield return null;
                }
            }
            Capture.DestroyRig(_rig); _rig = null;
            _renderDone = true;
            _signal.Set();
        }

        /// <summary>Faces whose pixels have arrived go to the worker, in order. False when one failed.</summary>
        private bool Collect()
        {
            while (_collected < _issued)
            {
                var f = _faces[_collected];
                if (!f.Collect()) return true;
                if (f.Error) { Capture.DisableAsync(); Fail("the GPU could not hand back face " + f.Face + " of viewpoint " + f.PointIndex + "; captures will wait for the GPU from now on"); return false; }
                _collected++;
                if (_rig.Rays)
                {
                    // Depth from physics rays: main thread only, and rare (no GPU depth read agreed with the rays).
                    var pt = _points[f.PointIndex];
                    f.Composed = Capture.Compose(f, pt, _far, _exposure, _waterLevel);
                    f.Composed.Depth = Capture.RayDepthPerPixel(f.Pos, f.Rot, f.Composed.Sky, pt.Res, pt.Step, pt.DepthRange);
                    f.Release();
                }
                lock (_toProcess) _toProcess.Enqueue(f);
                _signal.Set();
            }
            return true;
        }

        private void Fail(string why)
        {
            if (Error == null) Error = why;
            _abort = true;
            _signal.Set();
            if (_hidden != null) _hidden.Show();
            if (_rig != null) { Capture.DestroyRig(_rig); _rig = null; }
            if (_worker == null) Done = true;
        }

        internal void Abort() => Fail("aborted");

        // ---- worker thread ----
        private void Work()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                bool cleared = false;
                var grids = new FaceGrids[_points.Count][];
                for (int k = 0; k < grids.Length; k++) grids[k] = new FaceGrids[6];
                while (true)
                {
                    FaceRaw f = null;
                    lock (_toProcess) if (_toProcess.Count > 0) f = _toProcess.Dequeue();
                    if (f == null)
                    {
                        if (_abort) return;
                        if (_renderDone) break;
                        _signal.WaitOne(200);
                        continue;
                    }
                    if (!cleared) { Storage.Clear(_job); cleared = true; }
                    var pt = _points[f.PointIndex];
                    var raw = f.Composed ?? Capture.Compose(f, pt, _far, _exposure, _waterLevel);
                    f.Composed = null;
                    f.Release();
                    var layers = Layers.Process(raw, pt.Res, pt.Step, pt.DepthRange);
                    layers.Grids.Tan = pt.FaceTan;
                    Storage.SaveFace(_job, f.PointIndex, f.Face, layers, pt.Res);
                    grids[f.PointIndex][f.Face] = layers.Grids;
                    if (layers.Front != null) FrontFaces++;
                }
                if (_abort) return;
                for (int k = 0; k < _points.Count; k++) Storage.SavePoint(_job, k, _points[k], grids[k]);
                Storage.SaveGrass(_job, _grass);
                Storage.SaveFire(_job, _fire);
                Storage.Finish(_job, _points.Count, TakenAt);
            }
            catch (Exception e) { if (Error == null) Error = e.ToString(); }
            finally
            {
                WorkSeconds = sw.Elapsed.TotalSeconds;
                Done = true;
            }
        }
    }

    /// <summary>The renders of one cube face on their way from the GPU to the worker thread.</summary>
    internal class FaceRaw
    {
        public int PointIndex, Face;
        public Vector3 Pos;
        public Quaternion Rot;
        public bool NeedGpu, Flip;
        public Color32[] RawCol, SkyA, SkyB; // colour with fog; the black- and white-cleared sky-mask pair
        public Color32[] RawLocal;           // colour by local lights and emission only (null when not captured)
        public Color32[] RawNoSun;           // colour with only the sun switched off (null when not captured)
        public float[] Gpu;                  // device depth
        public Color32[] FlameMask;          // the flames rendered alone (null when there are none)
        public float[] ProxyGpu;             // device depth of the flames' stand-ins
        public RawFace Composed;             // only when depth comes from rays (main thread)
        public readonly AsyncGPUReadbackRequest[] Req = new AsyncGPUReadbackRequest[8];
        public readonly bool[] Issued = new bool[8];
        public bool Error;

        public bool Ready => RawCol != null && SkyA != null && SkyB != null && (!NeedGpu || Gpu != null);

        public void Set(int slot, Color32[] px)
        {
            if (slot == 0) RawCol = px; else if (slot == 1) SkyA = px; else if (slot == 2) SkyB = px; else if (slot == 4) RawLocal = px; else if (slot == 7) RawNoSun = px; else FlameMask = px;
        }

        public void SetDepth(int slot, float[] d)
        {
            if (slot == 3) Gpu = d; else ProxyGpu = d;
        }

        /// <summary>Main thread: take whatever has arrived. True once everything is here, or something failed.</summary>
        public bool Collect()
        {
            for (int s = 0; s < 8; s++)
            {
                if (!Issued[s]) continue;
                var r = Req[s];
                if (!r.done) return false;
                if (r.hasError) { Error = true; return true; }
                if (s == 3 || s == 6) SetDepth(s, r.GetData<float>().ToArray()); else Set(s, r.GetData<Color32>().ToArray());
                Issued[s] = false;
            }
            return Ready;
        }

        public void Release() { RawCol = SkyA = SkyB = RawLocal = RawNoSun = FlameMask = null; Gpu = null; ProxyGpu = null; }
    }
}
