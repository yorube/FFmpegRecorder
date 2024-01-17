using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor.Recorder;
using UnityEditor.Recorder.Encoder;
using UnityEditor.Recorder.Input;
using UnityEngine;

namespace Ruccho.FFmpegRecorder
{
    [RecorderSettings(typeof(FFmpegRecorder), "FFmpeg")]
    public class FFmpegRecorderSettings : RecorderSettings
    {
        public ImageInputSelector ImageInputSelector => imageInputSelector;
        [SerializeField] ImageInputSelector imageInputSelector = new ImageInputSelector();

        public AudioInputSettings AudioInputSettings => audioInputSettings;
        [SerializeField] AudioInputSettings audioInputSettings = new AudioInputSettings();

        public string FFmpegExecutablePath => ffmpegExecutablePath;
        [SerializeField] private string ffmpegExecutablePath = default;
        public string OutputExtension => extension;
        public bool UsePreset = false;

        [SerializeField] FFmpegPreset preset;
        public FFmpegPreset Preset => preset;

        [SerializeField] private string extension = "mp4";
        protected override string Extension => extension;
        [SerializeField] private bool usePreset = false;
        public string VideoCodec => videoCodec;
        [SerializeField] private string videoCodec;
        public string AudioCodec => audioCodec;
        [SerializeField] private string audioCodec = "aac";
        public int VideoBitrate => videoBitrate;
        [SerializeField] private int videoBitrate = 0;
        public string VideoArguments => videoArguments;
        [SerializeField] private string videoArguments;
        public string AudioArguments => audioArguments;
        [SerializeField] private string audioArguments;
        private int[] presetOptions;

        public override IEnumerable<RecorderInputSettings> InputsSettings
        {
            get
            {
                yield return imageInputSelector.Selected;
                yield return audioInputSettings;
            }
        }

        #region Refactor

        public bool CaptureAlpha
        {
            get { return captureAlpha; }
            set
            {
                captureAlpha = value;
                ImageInputSettings.RecordTransparency = value;
            }
        }

        [SerializeField] private bool captureAlpha;


        /// <summary>
        /// Indicates the Image Input Settings currently used for this Recorder.
        /// </summary>
        public ImageInputSettings ImageInputSettings
        {
            get => ImageInputSelector.ImageInputSettings;
            set => ImageInputSelector.ImageInputSettings = value;
        }

        /// <summary>
        /// The convention of the coordinate system for an encoder, to ensure that the images supplied to the encoder are flipped if needed.
        /// </summary>
        public enum EncoderCoordinateConvention
        {
            /// <summary>
            /// The origin is in the top left corner of each frame.
            /// </summary>
            OriginIsTopLeft,

            /// <summary>
            /// The origin is in the bottom left corner of each frame.
            /// </summary>
            OriginIsBottomLeft,
        }

        /// <summary>
        /// The output format of the FFmpeg encoder.
        /// </summary>
        public enum OutputFormat
        {
            [InspectorName("H.264 Default")] H264Default,
            [InspectorName("H.264 NVIDIA")] H264Nvidia,
            [InspectorName("H.264 Lossless 420")] H264Lossless420,
            [InspectorName("H.264 Lossless 444")] H264Lossless444,
            [InspectorName("H.265 HEVC Default")] HevcDefault,
            [InspectorName("H.265 HEVC NVIDIA")] HevcNvidia,

            [InspectorName("Apple ProRes 4444 XQ (ap4x)")]
            ProRes4444XQ,

            [InspectorName("Apple ProRes 4444 (ap4h)")]
            ProRes4444,

            [InspectorName("Apple ProRes 422 HQ (apch)")]
            ProRes422HQ,

            [InspectorName("Apple ProRes 422 (apcn)")]
            ProRes422,

            [InspectorName("Apple ProRes 422 LT (apcs)")]
            ProRes422LT,

            [InspectorName("Apple ProRes 422 Proxy (apco)")]
            ProRes422Proxy,
            [InspectorName("VP8 (WebM)")] VP8Default,
            [InspectorName("VP9 (WebM)")] VP9Default,
        }

        /// <summary>
        /// The format of the encoder.
        /// </summary>
        public OutputFormat Format
        {
            get => outputFormat;
            set => outputFormat = value;
        }

        [SerializeField] OutputFormat outputFormat;


        /// <summary>
        /// Indicates where the first pixel of the image will be in the frame.
        /// </summary>
        public EncoderCoordinateConvention CoordinateConvention => EncoderCoordinateConvention.OriginIsTopLeft;

        /// <summary>
        /// Populates the lists of errors and warnings for a given encoder context.
        /// </summary>
        /// <param name="ctx">The context of the current recording.</param>
        /// <param name="errors">The list of errors to append to.</param>
        /// <param name="warnings">The list of warnings to append to.</param>
        public void ValidateRecording(RecordingContext ctx, List<string> errors, List<string> warnings)
        {
            if (!File.Exists(ffmpegExecutablePath))
                errors.Add($"Cannot find the FFMPEG encoder at path: {ffmpegExecutablePath}");


            if (ctx.doCaptureAlpha && !CodecFormatSupportsAlphaChannel(Format))
                errors.Add($"Format '{Format}' does not support transparency.");

            if (ctx.frameRateMode == FrameRatePlayback.Variable)
                errors.Add(
                    $"This encoder does not support Variable frame rate playback. Please consider using Constant frame rate instead.");
        }

        /// <summary>
        /// Gets the texture format this encoder requires from the Recorder.
        /// </summary>
        /// <param name="inputContainsAlpha">Whether the encoder's input contains an alpha channel or not.</param>
        /// <returns></returns>
        public TextureFormat GetTextureFormat(bool inputContainsAlpha)
        {
            var codecFormatSupportsTransparency = CodecFormatSupportsAlphaChannel(Format);
            var willIncludeAlpha = inputContainsAlpha && codecFormatSupportsTransparency;
            return willIncludeAlpha ? TextureFormat.ARGB32 : TextureFormat.RGB24;
        }

        public string GetPixelFormat(bool inputContainsAlpha)
        {
            var codecFormatSupportsTransparency = CodecFormatSupportsAlphaChannel(Format);
            var willIncludeAlpha = inputContainsAlpha && codecFormatSupportsTransparency;
            return willIncludeAlpha ? "argb" : "rgb24";
        }

        public bool CanCaptureAlpha => CodecFormatSupportsAlphaChannel(Format);

        public bool CanCaptureAudio => true;

        /// <summary>
        /// Indicates whether the requested ProRes codec format can encode an alpha channel or not.
        /// </summary>
        /// <param name="format">The ProRes codec format to check.</param>
        /// <returns>True if the specified codec can encode an alpha channel, False otherwise.</returns>
        internal bool CodecFormatSupportsAlphaChannel(OutputFormat format)
        {
            return format == OutputFormat.ProRes4444XQ || format == OutputFormat.ProRes4444;
        }

        /// <summary>
        /// Returns a RecordingContext for the current settings.
        /// </summary>
        /// <returns>A RecordingContext populated with fields that the MovieRecorderSettings controls.</returns>
        /// <remarks>
        /// Not all fields of the RecordingContext are populated (e.g. path).
        /// </remarks>
        /// <exception cref="InvalidCastException">Thrown if the input is not recognized.</exception>
        internal RecordingContext GetRecordingContext()
        {
            RecordingContext ctx = default;
            if (this == null)
            {
                return ctx;
            }

            ctx.frameRateMode = FrameRatePlayback;
            ctx.doCaptureAlpha = ImageInputSettings.SupportsTransparent && CaptureAlpha;
            ctx.doCaptureAudio = audioInputSettings.PreserveAudio;
            ctx.fps = FFmpegRecorderUtil.RationalFromDouble(FrameRate);
            var inputSettings = InputsSettings.First();
            if (inputSettings is GameViewInputSettings gvs)
            {
                ctx.height = gvs.OutputHeight;
                ctx.width = gvs.OutputWidth;
            }
            else if (inputSettings is CameraInputSettings cs)
            {
                ctx.height = cs.OutputHeight;
                ctx.width = cs.OutputWidth;
            }
            else if (inputSettings is Camera360InputSettings cs3)
            {
                ctx.height = cs3.OutputHeight;
                ctx.width = cs3.OutputWidth;
            }
            else if (inputSettings is RenderTextureInputSettings rts)
            {
                ctx.height = rts.OutputHeight;
                ctx.width = rts.OutputWidth;
            }
            else if (inputSettings is RenderTextureSamplerSettings ss)
            {
                ctx.height = ss.OutputHeight;
                ctx.width = ss.OutputWidth;
            }
            else
            {
                throw new InvalidCastException($"Unexpected type of input settings");
            }

            return ctx;
        }

        #endregion
    }
}