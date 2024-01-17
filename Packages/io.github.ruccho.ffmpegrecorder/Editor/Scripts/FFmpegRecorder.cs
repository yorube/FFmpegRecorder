using System;
using System.IO;
using System.Threading;
using Unity.Collections;
using UnityEditor;
using UnityEditor.Media;
using UnityEditor.Recorder;
using UnityEditor.Recorder.Encoder;
using UnityEditor.Recorder.Input;
using UnityEngine;
using UnityEngine.Rendering;


namespace Ruccho.FFmpegRecorder
{
    public class FFmpegRecorder : BaseTextureRecorder<FFmpegRecorderSettings>
    {
        protected override TextureFormat ReadbackTextureFormat => Settings.GetTextureFormat(
            Settings.CaptureAlpha && Settings.CanCaptureAlpha &&
            Settings.ImageInputSettings.SupportsTransparent);

        private string AbsoluteFilename { get; set; }

#if UNITY_RECORDER_4_OR_NEWER
        public AudioInput AudioInput => m_Inputs[1] as AudioInput ??
                                        throw new InvalidOperationException("Failed to get AudioInput instance.");
#else
        public RecorderInput AudioInput => m_Inputs[1];
#endif

        private NativeArray<float> tempAudioBuffer;

        private void GetAudioBuffer(ref NativeArray<float> userArray, out int writtenSize)
        {
#if UNITY_RECORDER_4_OR_NEWER
            AudioInput.GetBuffer(ref userArray, out writtenSize);
#else
            WarmupAudioBufferAccess();
            var buff = AudioInputMainBufferGet(AudioInput);
            if (userArray.Length < buff.Length)
                throw new ArgumentException(
                    $"The supplied array (size {userArray.Length}) must be larger than or of the same size as the audio sample buffer (size {buff.Length})");

            userArray.GetSubArray(0, buff.Length).CopyFrom(buff);
            writtenSize = buff.Length;
#endif
        }

        private int GetAudioBufferSize()
        {
#if UNITY_RECORDER_4_OR_NEWER
            return AudioInput.GetBufferSize();
#else
            WarmupAudioBufferAccess();
            return AudioInputMainBufferGet(AudioInput).Length;
#endif
        }

#if !UNITY_RECORDER_4_OR_NEWER
        private Func<RecorderInput, NativeArray<float>> AudioInputMainBufferGet { get; set; }
#endif
        private void WarmupAudioBufferAccess()
        {
#if !UNITY_RECORDER_4_OR_NEWER
            if (AudioInputMainBufferGet != null) return;
            
            var audioInputType = typeof(Recorder).Assembly.GetType("UnityEditor.Recorder.Input.AudioInput");
            if (audioInputType == null) throw new InvalidOperationException("AudioInput Type was not found.");
            
            var audioInputParameterEx = Expression.Parameter(typeof(RecorderInput), "audioInput");

            var audioInputEx = Expression.TypeAs(audioInputParameterEx, audioInputType);

            var audioInputMainBufferEx =
                Expression.MakeMemberAccess(audioInputEx, audioInputType.GetProperty("mainBuffer"));

            var audioInputMainBufferGetEx = Expression.Lambda<Func<RecorderInput, NativeArray<float>>>(
                audioInputMainBufferEx, audioInputParameterEx);

            AudioInputMainBufferGet = audioInputMainBufferGetEx.Compile();
#endif
        }
        
        protected override bool BeginRecording(RecordingSession session)
        {
            if (!base.BeginRecording(session))
                return false;

            try
            {
                Settings.FileNameGenerator.CreateDirectory(session);
            }
            catch (Exception)
            {
                Debug.LogError(string.Format("FFmpeg recorder output directory \"{0}\" could not be created.",
                    Settings.FileNameGenerator.BuildAbsolutePath(session)));
                return false;
            }

            var input = m_Inputs[0] as BaseRenderTextureInput;
            if (input == null)
            {
                Debug.LogError("FFmpegRecorder could not find input.");
                return false;
            }

            int width = input.OutputWidth;
            int height = input.OutputHeight;

            if (width <= 0 || height <= 0)
            {
                Debug.LogError(string.Format("FFmpegRecorder got invalid input resolution {0} x {1}.", width, height));
                return false;
            }

            WarmupAudioBufferAccess();

            try
            {
                //Check project audioManager setting is disable(e.g some project using other audio system it will be disable)
                //This prevent crash when CreateAudioProcess but audio setting is disabled.
                var audioManager = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/AudioManager.asset")[0];
                var serializedManager = new SerializedObject(audioManager);
                var isProjectSettingsDisableAudio = serializedManager.FindProperty("m_DisableAudio").boolValue;

                bool mux = !Settings.CanCaptureAudio || isProjectSettingsDisableAudio;
                var frameRate = session.settings.FrameRatePlayback == FrameRatePlayback.Constant
                    ? FFmpegRecorderUtil.RationalFromDouble(session.settings.FrameRate)
                    : new MediaRational { numerator = 0, denominator = 0 };
                // Get a recording context
                var recordingContext = Settings.GetRecordingContext();
                // Set locally determined fields
                recordingContext.path = Settings.FileNameGenerator.BuildAbsolutePath(session);
                recordingContext.fps = frameRate;
                CreateVideoProcess(recordingContext, !mux);
                if (mux)
                {
                    CreateAudioProcess(recordingContext.path);
                }

                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError("MovieRecorder unable to create MovieEncoder. " + ex.Message);
                return false;
            }
        }

        private FFmpegHost videoProcess;

        private void CreateVideoProcess(RecordingContext context, bool withoutMux = false)
        {
            videoProcess?.Dispose();
            var pixel = Settings.GetPixelFormat(context.doCaptureAlpha);

            var videoBitrate = Settings.VideoBitrate <= 0
                ? ""
                : $" -b:v {Mathf.Clamp(Settings.VideoBitrate * 1000, 0, float.PositiveInfinity)}";
            var videoCodec = string.IsNullOrEmpty(Settings.VideoCodec) ? "" : $" -c:v {Settings.VideoCodec}";
            var arguments = "-y -f rawvideo -vcodec rawvideo"
                            + " -pixel_format " + pixel
                            + " -colorspace bt709"
                            + " -video_size " + context.width + "x" + context.height
                            + " -framerate " + (float)FFmpegRecorderUtil.DoubleFromRational(context.fps)
                            + " -loglevel error -i - "
                            + videoBitrate
                            + videoCodec
                            + $" {Settings.VideoArguments}"
                            + (withoutMux
                                ? $" \"{context.path}\""
                                : $" \"{context.path.Split('.')[0]}_video.{Settings.OutputExtension}\"");
            videoProcess = new FFmpegHost(
                Settings.FFmpegExecutablePath,
                arguments);
        }

        private FFmpegHost audioProcess;

        private void CreateAudioProcess(string outputPath)
        {
            audioProcess?.Dispose();

            // from Unity Recorder 4.0.0, AudioInput
            int audioOutputSampleRate;
            ushort audioOutputChannelCount;

#if UNITY_RECORDER_4_OR_NEWER
            audioOutputSampleRate = new MediaRational(AudioSettings.outputSampleRate).numerator;
            audioOutputChannelCount = AudioInput.ChannelCount;
#else
            audioInputSampleRate = (int) audioInputType.GetProperty("sampleRate").GetValue(audioInput);
            audioInputChannelCount = (ushort) audioInputType.GetProperty("channelCount").GetValue(audioInput);
#endif

            var audioCodec = string.IsNullOrEmpty(Settings.AudioCodec) ? "" : $" -acodec {Settings.AudioCodec}";
            var audioArguments =
                $"-y -f f32le -ar {audioOutputSampleRate} -ac {audioOutputChannelCount}"
                + " -loglevel error -i - "
                + audioCodec
                + $" -ar {audioOutputSampleRate} -ac {audioOutputChannelCount}"
                + $" {Settings.AudioArguments}"
                + $" \"{outputPath}_audio.{Settings.OutputExtension}\"";
            audioProcess = new FFmpegHost(Settings.FFmpegExecutablePath, audioArguments);
        }

        private byte[] nativeArrayBuffer;

        protected override void WriteFrame(Texture2D t)
        {
            NativeArray<byte> cols = t.GetRawTextureData<byte>();
            InternalWriteFrame(cols);
        }

#if UNITY_2019_1_OR_NEWER
        protected override void WriteFrame(AsyncGPUReadbackRequest r)
        {
            var raw = r.GetData<byte>();
            InternalWriteFrame(raw);
        }

#endif

        private void InternalWriteFrame(NativeArray<byte> data)
        {
            if (nativeArrayBuffer == null || nativeArrayBuffer.Length != data.Length)
            {
                nativeArrayBuffer = new byte[data.Length];
            }

            data.CopyTo(nativeArrayBuffer);

            var input = m_Inputs[0] as BaseRenderTextureInput;
            int width = input.OutputWidth;
            int height = input.OutputHeight;

            if (videoProcess != null && videoProcess.StdIn != null)
            {
                if (videoProcess.StdIn.BaseStream.CanWrite)
                {
                    videoProcess.StdIn.BaseStream.Write(nativeArrayBuffer, 0, nativeArrayBuffer.Length);
                    videoProcess.StdIn.BaseStream.Flush();
                }
            }
        }

        protected override void RecordFrame(RecordingSession session)
        {
            base.RecordFrame(session);


            bool mux = Settings.AudioInputSettings.PreserveAudio;


            if (mux && (videoProcess == null || audioProcess == null || videoProcess.FFmpeg.HasExited ||
                        audioProcess.FFmpeg.HasExited)
                || (!mux && (videoProcess == null || videoProcess.FFmpeg.HasExited)))
            {
                AbortRecording(session);
                return;
            }

            if (mux)
            {
                var length = GetAudioBufferSize();

                if (!tempAudioBuffer.IsCreated || tempAudioBuffer.Length < length)
                {
                    tempAudioBuffer = new NativeArray<float>(length, Allocator.Persistent);
                }


                GetAudioBuffer(ref tempAudioBuffer, out length);

#if NET_STANDARD_2_1
                // Use Span
                ReadOnlySpan<float> tempAudioBufferSpan;
#if UNITY_2022_2_OR_NEWER
                // In Unity 2022.2 or newer, NativeArrays can be converted into spans.
                tempAudioBufferSpan = tempAudioBuffer.AsReadOnlySpan().Slice(0, length);
#else
                unsafe
                {
                    tempAudioBufferSpan = new ReadOnlySpan<float>(tempAudioBuffer.GetUnsafePtr(), length);
                }
#endif
                
                audioProcess.StdIn.BaseStream.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(tempAudioBufferSpan));
#else
                // Write each elements
                for (int i = 0; i < length; i++)
                {
                    var bytes = BitConverter.GetBytes(tempAudioBuffer[i]);
                    audioProcess.StdIn.BaseStream.Write(bytes, 0, bytes.Length);
                }
#endif
                audioProcess.StdIn.BaseStream.Flush();
            }
        }

        private string CloseProcess(FFmpegHost process)
        {
            if (process != null)
            {
                if (process.StdIn != null)
                {
                    process.StdIn.Close();
                }

                process.FFmpeg.WaitForExit();
                var outputReader = process.FFmpeg.StandardError;
                var error = outputReader.ReadToEnd();
                return error;
            }

            return null;
        }

        private void AbortRecording(RecordingSession session)
        {
            Debug.LogError("FFmpeg has exited.");

            var videoError = CloseProcess(videoProcess);
            if (!string.IsNullOrEmpty(videoError)) Debug.LogError("Video encoding process: " + videoError);

            videoProcess?.Dispose();
            videoProcess = null;

            var audioError = CloseProcess(videoProcess);
            if (!string.IsNullOrEmpty(videoError)) Debug.LogError("Video encoding process: " + audioError);

            audioProcess?.Dispose();
            audioProcess = null;

            session.Dispose();
        }

        protected override void DisposeEncoder()
        {
            base.DisposeEncoder();

            Debug.Log("FFmpeg Recorder: End Encoder");

            bool mux = Settings.AudioInputSettings.PreserveAudio;

            if (audioProcess != null && videoProcess != null)
            {
                var videoError = CloseProcess(videoProcess);
                if (!string.IsNullOrEmpty(videoError)) Debug.LogError("Video encoding process: " + videoError);
                var audioError = CloseProcess(audioProcess);
                if (!string.IsNullOrEmpty(videoError)) Debug.LogError("Audio encoding process: " + audioError);

                if (mux)
                {
                    var muxProcess = new FFmpegHost(
                        Settings.FFmpegExecutablePath,
                        $"-y -loglevel error" +
                        $" -i \"{AbsoluteFilename}_audio.{Settings.OutputExtension}\" -i \"{AbsoluteFilename}_video.{Settings.OutputExtension}\"" +
                        $" -c:v copy -c:a copy" +
                        $" \"{AbsoluteFilename}\""
                        , true);
                    Debug.Log("FFmpeg Recorder: Start muxing");

                    muxProcess.FFmpeg.EnableRaisingEvents = true;
                    SynchronizationContext c = SynchronizationContext.Current;
                    muxProcess.FFmpeg.Exited += (obj, e) =>
                    {
                        c.Post((_) =>
                        {
                            muxProcess.FFmpeg.WaitForExit();
                            var outputReader = muxProcess.FFmpeg.StandardError;
                            var error = outputReader.ReadToEnd();
                            if (!string.IsNullOrEmpty(error)) Debug.LogError("Muxing process: " + error);
                            else
                            {
                                File.Delete($"{AbsoluteFilename}_audio.{Settings.OutputExtension}");
                                File.Delete($"{AbsoluteFilename}_video.{Settings.OutputExtension}");
                            }

                            Debug.Log("FFmpeg Recorder: End muxing");
                        }, false);
                    };
                }

                tempAudioBuffer.Dispose();

                videoProcess?.Dispose();
                audioProcess?.Dispose();
                videoProcess = null;
                audioProcess = null;
            }
        }
    }
}