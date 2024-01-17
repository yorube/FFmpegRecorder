﻿using System;
using System.IO;

namespace Ruccho.FFmpegRecorder
{
    public class FFmpegHost : IDisposable
    {
        public System.Diagnostics.Process FFmpeg { get; private set; }
        public StreamWriter StdIn => FFmpeg?.StandardInput;
        public StreamReader StdOut => FFmpeg?.StandardOutput;
        public StreamReader StdErr => FFmpeg?.StandardError;


        public FFmpegHost(string executable, string arguments, bool redirect = true)
        {
            if (!File.Exists(executable))
            {
                throw new FileNotFoundException("Specify FFmpeg executable path in recorder settings.");
            }
            var psi = new System.Diagnostics.ProcessStartInfo()
            {
                Arguments = arguments,
                FileName = executable,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = redirect,
                RedirectStandardOutput = redirect,
                RedirectStandardError = redirect
            };

            FFmpeg = System.Diagnostics.Process.Start(psi);
            
        }

        public void Dispose()
        {
            FFmpeg?.Dispose();
        }
    }
}