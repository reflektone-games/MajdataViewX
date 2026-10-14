#nullable enable

using System;
using System.IO;
using System.Threading;
using Cimai;
using Cysharp.Threading.Tasks;
using MajdataViewX.Base;
using MajdataViewX.Notes;
using MajdataViewX.Notes.SlideUtils;
using MajdataViewX.Types.MajSetting;
using UnityEngine;
using static MajdataViewX.Base.MajCtx;
using ThreadPriority = System.Threading.ThreadPriority;

namespace MajdataViewX.Managers
{
    public class PlayManager : MonoBehaviour
    {
        public static bool isBusy;
        public static bool isPlaying;
        public static bool filesLoaded;

        private static Thread? _audioManagerThread;
        private static int     _audioManagerThreadRunning;

        private static MajViewSetting _setting = new();
        private static float          _currentOffset;
        private static SimaiChart?    _chart;

        private SpriteRenderer _bgCover        = null!;
        private SpriteRenderer _bgOutsideCover = null!;
        private GameObject     _canvasButtons  = null!;

        private void Awake()
        {
            _playManager = this;
        }

        // 这里是游戏内部的东西的启动初始化
        private void Start()
        {
            _bgCover        = GameObject.Find("BgCover").GetComponent<SpriteRenderer>();
            _bgOutsideCover = GameObject.Find("BgOutsideCover").GetComponent<SpriteRenderer>();
            _canvasButtons  = GameObject.Find("CanvasButtons");

            _ = new AudioManager();
            Volatile.Write(ref _audioManagerThreadRunning, 1);
            _audioManagerThread = new Thread(() =>
            {
                while (Volatile.Read(ref _audioManagerThreadRunning) != 0)
                {
                    _audioManager.OnUpdate();
                    Thread.Sleep(1);
                }
            })
            {
                IsBackground = true,
                Name         = "Majdata SFX Trigger",
                Priority     = ThreadPriority.AboveNormal,
            };
            _audioManagerThread.Start();

            MajBurst.__DataSS.Data = new MajBurstData
            {
                TimeData         = new(),
                InputData        = new(),
                MultTouchHandler = new(),
                GlobalRandom     = new((uint)"MajdataX".GetHashCode()),
            };
            //MajBurst.TimeData.Init();
            MajBurst.InputData.Init();
            MajBurst.MultTouchHandler.Init();

            _ = new InputManager();


            SlideTableNeo.InitializeStandardSlideTable();
        }

        private bool CheckIsLoaded() => _audioManager.IsTrackLoaded &&
                                        _bgManager.IsBgLoaded       &&
                                        _bgManager.IsVideoLoaded;

        public async UniTask LoadAsync(string audioPath, string bgPath, string? pvPath)
        {
            await UniTask.WaitUntil(() => !isBusy);

            try
            {
                await UniTask.SwitchToMainThread();

                _audioManager.LoadTrack(audioPath);

                if (File.Exists(bgPath))
                {
                    BgManager.HasBg = true;
                    _bgManager.LoadBg(bgPath);
                }
                else
                {
                    BgManager.HasBg = false;
                }

                if (pvPath is not null && File.Exists(pvPath))
                {
                    BgManager.HasVideo = true;
                    _bgManager.LoadVideo(pvPath);
                }
                else
                {
                    BgManager.HasVideo = false;
                }

                filesLoaded = true;
            }
            catch (Exception)
            {
                filesLoaded = false;
            }
        }

        public void Setting(MajViewSetting setting, MajVolumeSetting volumeSetting)
        {
            _setting = setting;

            NoteHelper.NoteSettingsSS.Data = new NoteSettings
            {
                AutoPlayMode     = _setting.AutoMode,
                TapSpeed         = (float)(107.25 / (71.4184491 * Mathf.Pow(_setting.TapSpeed + 0.9975f, -0.985558604f))),
                TouchSpeed       = _setting.TouchSpeed,
                LegacySlideLayer = _setting.LegacySlideLayer,
                SmoothSlideAnime = _setting.SmoothSlideAnime,
                MineAutoSlide    = _setting.MineAutoSlide,
            };
            //audio
            _audioManager.Setting(setting.GlobalAudioOffset, volumeSetting);
            //simulate
            _inputManager.ShowHand = _setting.ShowHand;
            //counter
            _objectCounter.Setting(_setting.ComboStatusType, _setting.UIType);
            //bg
            _bgCover.color        = new Color(0f, 0f, 0f, _setting.BackgroundDim);
            _bgOutsideCover.color = new Color(0f, 0f, 0f, _setting.BackgroundOutsideDim);
            _bgManager.resizeBg   = _setting.ResizeBg;
        }

        public async UniTask ParseAsync(
            string? chartText,
            int     clockCount,
            double  offset,
            string  title,
            string  artist,
            string  level,
            string  designer,
            int     difficulty)
        {
            await UniTask.WaitUntil(() => !isBusy);
            isBusy = true;

            var chart = SimaiChart.Parse(chartText ?? string.Empty);

            _chart?.Dispose();
            _chart               = chart;
            _currentOffset       = (float)offset;
            _timeProvider.offset = (float)offset;

            //answer
            _audioManager.GenerateAnswerSFX(chart, clockCount);

            //counter
            _objectCounter.ResetLoaded();
            _objectCounter.CountNoteSum(chart);
            _objectCounter.ReportMeterBpm(chart);

            await _dataLoader.Load(chart, title, artist, level, designer, difficulty);
            isBusy = false;
        }

        public async UniTask PlayAsync(double startAt, bool showIntro, string recordPath)
        {
            await UniTask.WaitUntil(() => !isBusy);

            if (!filesLoaded || isPlaying)
                return;

            isBusy = true;
            var isRecording = string.IsNullOrEmpty(recordPath);

            try
            {
                await UniTask.SwitchToMainThread();

                var ignoreOffset = startAt - _currentOffset;

                _timeProvider.ResetState();
                _audioManager.ResetState();
                _bgManager.ResetState();
                _effectManager.ResetState();
                _allPerfectManager.ResetState();

                //bg
                _bgManager.ShowBg();
                _bgManager.ShowVideo();
                //sfx
                _audioManager.ResetAnswerSFX(ignoreOffset);
                //counter
                _objectCounter.ResetCur();
                _objectCounter.CountIgnoreNoteCountAsync(_chart, ignoreOffset);
                //notes
                //MajBurst.InputData.ResetState(); in ResetLoadedNote
                _noteManager.ResetState(); //reset DJAuto hands (PlayUpdateJob is still running when IsStart==false)
                _noteManager.ResetLoadedNote(ignoreOffset);
                _noteManager.ResetLoadedPlay(ignoreOffset);
                MajBurst.MultTouchHandler.ResetMultTouchState();

                _allPerfectManager.enabled = showIntro;

                if (showIntro)
                {
                    _audioManager.noteSfxPlaybackRequests[AudioManager.TRACK_START] = true;
                    _bgManager.PlaySongDetail();
                }

                if (isRecording)
                {
                    _screenRecorder.StartRecording(recordPath, _setting.OutputFps, _setting.ExportQuality,
                                                   () =>
                                                   {
                                                       _timeProvider.SetStartTime(
                                                           startAt, _currentOffset, showIntro, _setting.OutputFps
                                                       );
                                                   })
                                   .ContinueWith(() =>
                                   {
                                       _canvasButtons.SetActive(true);
                                       isPlaying = false;
                                   }).Forget();
                }
                else
                {
                    _canvasButtons.SetActive(true);
                    _audioManager.PlayTrack();
                    _timeProvider.SetStartTime(startAt, _currentOffset, showIntro);
                }

                isPlaying = true;
            }
            catch (Exception)
            {
                isPlaying = false;
            }
            finally
            {
                isBusy = false;
            }
        }

        public async UniTask PauseAsync()
        {
            await UniTask.WaitUntil(() => !isBusy);

            if (isPlaying)
                return;

            isBusy = true;

            try
            {
                await UniTask.SwitchToMainThread();

                _screenRecorder.StopRecording();
                await UniTask.Yield();

                _timeProvider.Pause();

                _bgManager.PauseVideo();

                _audioManager.PauseTrack();
                _audioManager.PauseTouchHoldSound();

                isPlaying = false;
            }
            finally
            {
                isBusy = false;
            }
        }


        public async UniTask UnloadAsync()
        {
            await UniTask.WaitUntil(() => !isBusy);
            isBusy = true;

            try
            {
                await UniTask.SwitchToMainThread();

                _screenRecorder.StopRecording();
                await UniTask.Yield();

                //_objectCounter.ResetCur();
                _timeProvider.ResetState();
                _audioManager.ResetState();
                _bgManager.ResetState();
                _effectManager.ResetState();
                _allPerfectManager.ResetState();
            }
            finally
            {
                isBusy = false;
            }
        }

        private void OnDestroy()
        {
            Volatile.Write(ref _audioManagerThreadRunning, 0);
            if (_audioManagerThread is { IsAlive: true } &&
                _audioManagerThread != Thread.CurrentThread)
            {
                _audioManagerThread.Join();
            }

            _audioManagerThread = null;

            _audioManager.OnDestroy();
            _inputManager.OnDestroy();
            MajBurst.InputData.Dispose();
        }
    }
}
