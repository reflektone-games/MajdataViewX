using Cimai;
using MajdataViewX.Base;
using System;
using System.Diagnostics;
using System.IO;
#if !UNITY_ANDROID
// Android 平台不支持 MemoryMappedFile，相关代码全部排除
using System.IO.MemoryMappedFiles;
#endif
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using static MajdataViewX.Base.MajBurst;
using static MajdataViewX.Base.MajCtx;

namespace MajdataViewX.Managers
{
    public class TimeProvider : MonoBehaviour
    {
        public bool IsStart  { get; private set; }
        public bool IsRecord { get; private set; }

        //audio get this value
        public float AudioTime { get; private set; }

        //notes get this value
        public float NoteTime     { get; private set; }
        public float FakeNoteTime => TimeData.GetPositionAtTime(NoteTime);
        public float CurrentSpeed => IsRecord ? Time.timeScale : _speed;

        private static NativeList<(float time, float sVeloc)> _svList = new(20, Allocator.Persistent);
        private static NativeArray<(float k, float b)>        _svFuncArgs;

        private double _startRealtime; //the beginning of the program is 0
        private float  _startAt;       //the beginning of the audio is 0
        public  float  offset;

        private float _speed;

        //for pause and resume
        private double _accumulated;

        private readonly Stopwatch _playbackClock = new();


#if !UNITY_ANDROID
        private MemoryMappedFile         _mmfAudioTime;
        private MemoryMappedViewAccessor _mmvAudioTime;
#endif

        public const float SongDetailOffset = 5f;

        private void Awake()
        {
            _timeProvider = this;
            ResetState();

#if !UNITY_ANDROID
            var mmfAudioTimeFileStream = new FileStream(
                MajEnv.MmfAudioTimePath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.ReadWrite
            );
            if (mmfAudioTimeFileStream.Length < sizeof(float))
                mmfAudioTimeFileStream.SetLength(sizeof(float));
            _mmfAudioTime = MemoryMappedFile.CreateFromFile(
                mmfAudioTimeFileStream,
                null,
                sizeof(float),
                MemoryMappedFileAccess.ReadWrite,
                HandleInheritability.None,
                false
            );
            _mmvAudioTime = _mmfAudioTime.CreateViewAccessor();
#endif
        }

        private void Update()
        {
            TimeData.isStart   = IsStart;
            TimeData.deltaTime = IsRecord ? FRAME_LENGTH_SEC : Time.deltaTime;
            if (!IsStart)
            {
#if !UNITY_ANDROID
                _mmvAudioTime.Read(0, out float time);
                AudioTime         = time;
                NoteTime          = AudioTime - offset;
                TimeData.noteTime = NoteTime;
#endif
                return;
            }

            if (IsRecord)
            {
                AudioTime = (float)(_startAt + _accumulated + (Time.timeAsDouble - _startRealtime));
                NoteTime  = AudioTime - offset;
            }
            else
            {
                AudioTime = (float)(_startAt + _accumulated + _playbackClock.Elapsed.TotalSeconds * _speed);
                NoteTime  = AudioTime - offset;
            }

#if !UNITY_ANDROID
            _mmvAudioTime.Write(0, AudioTime);
#endif

            TimeData.noteTime = NoteTime;
        }

        public unsafe void LoadSV(ReadOnlySpan<SimaiTiming> commaTimings)
        {
            _svList.Clear();
            if (_svFuncArgs.IsCreated) _svFuncArgs.Dispose();
            foreach (var timing in commaTimings)
            {
                if (_svList.Length == 0 || _svList[^1].sVeloc != timing.SVeloc)
                {
                    _svList.Add(((float)timing.Time, timing.SVeloc));
                }
            }

            if (_svList.Length == 0)
            {
                return;
            }

            _svFuncArgs = new(_svList.Length + 1, Allocator.Persistent);

            float pos       = 0f;
            float lastTime  = 0f;
            float lastSpeed = 1f;

            _svFuncArgs[0] = (1, 0);

            for (var i = 0; i < _svList.Length; i++)
            {
                var (time, sveloc) =  _svList[i];
                pos                += lastSpeed * (time - lastTime);

                // 使用当前点的时间和当前新的速度构建往后的运动方程
                _svFuncArgs[i + 1] = (sveloc, pos - sveloc * time);
                lastTime           = time;
                lastSpeed          = sveloc;
            }

            TimeData.svListPtr        = _svList.GetUnsafeReadOnlyPtr();
            TimeData.svListLength     = _svList.Length;
            TimeData.svFuncArgsPtr    = _svFuncArgs.GetUnsafeReadOnlyPtr();
            TimeData.svFuncArgsLength = _svFuncArgs.Length;
        }

        public void SetStartTime(double startAt, double offset, bool showIntro, int captureFramerate = 0)
        {
            IsRecord     = false;
            AudioTime    = 0f;
            NoteTime     = 0f;
            _accumulated = 0f;
            _playbackClock.Reset();
            Time.timeScale        = 1f;
            Time.captureFramerate = captureFramerate;

            this._startAt = (float)startAt;
            this.offset   = (float)offset;

            if (showIntro)
            {
                _startAt -= SongDetailOffset;
            }

            _playbackClock.Restart();

            IsStart = true;
            //calculate immediately
            Update();
        }

        public void Pause()
        {
            if (!IsStart) return;

            var now = IsRecord ? Time.timeAsDouble : Time.realtimeSinceStartupAsDouble;
            _accumulated += IsRecord
                ? now - _startRealtime
                : _playbackClock.Elapsed.TotalSeconds * _speed;

            _playbackClock.Reset();
            IsStart = false;
        }

        public void Resume()
        {
            if (IsStart) return;

            if (IsRecord)
                _startRealtime = Time.timeAsDouble;
            else
                _playbackClock.Restart();

            IsStart = true;
        }

        public void ResetState()
        {
            IsStart        = false;
            IsRecord       = false;
            AudioTime      = 0f;
            NoteTime       = 0f;
            _startRealtime = 0f;
            _startAt       = 0f;
            offset         = 0f;
            _accumulated   = 0f;
            _speed         = 1f;
            _playbackClock.Reset();
            Time.timeScale        = 1f;
            Time.captureFramerate = 0;
        }

        private void OnDestroy()
        {
#if !UNITY_ANDROID
            _mmvAudioTime?.Dispose();
            _mmfAudioTime?.Dispose();
#endif

            if (_svList.IsCreated) _svList.Dispose();
            if (_svFuncArgs.IsCreated) _svFuncArgs.Dispose();
        }
    }

    [BurstCompile]
    public struct TimeDataB
    {
        public bool  isStart;
        public float noteTime;

        public readonly float FakeNoteTime => GetPositionAtTime(noteTime);

        // 存在减少late的隐秘指令
        public readonly float DjAutoTime => TimeData.noteTime + 0.01f;

        /// <summary>
        /// 录制模式时恒为FRAME_LENGTH_SEC，因此无法与Time.xxx配合使用！！！
        /// </summary>
        public float deltaTime;

        // public NativeList<(float time, float sVeloc)> SVList;
        // public NativeArray<(float k, float b)> SVFuncArgs;
        public unsafe void* svListPtr;
        public        int   svListLength;
        public unsafe void* svFuncArgsPtr;
        public        int   svFuncArgsLength;

        readonly unsafe ReadOnlySpan<(float time, float sVeloc)> SVList     => new(svListPtr, svListLength);
        readonly unsafe ReadOnlySpan<(float k, float b)>         SVFuncArgs => new(svFuncArgsPtr, svFuncArgsLength);


        public readonly float GetPositionAtTime(float t)
        {
            if (SVList.Length == 0)
                return t;
            if (t < SVList[0].time)
                return t;
            if (t >= SVList[^1].time)
                return SVFuncArgs[^1].k * t + SVFuncArgs[^1].b;

            // 二分查找
            int low  = 0;
            int high = SVList.Length - 1;
            while (low <= high)
            {
                int mid = (low + high) >> 1;
                if (t < SVList[mid].time)
                {
                    if (mid == 0 || t >= SVList[mid - 1].time) return SVFuncArgs[mid].k * t + SVFuncArgs[mid].b;
                    high = mid                                                              - 1;
                }
                else low = mid + 1;
            }

            return SVFuncArgs[^1].k * t + SVFuncArgs[^1].b;
        }

        public readonly float GetFrame() => noteTime * 1000 / 16.6667f;
    }
}
