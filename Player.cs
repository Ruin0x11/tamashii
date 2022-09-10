using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using System.Numerics;

namespace tamashii
{
    internal class Player
    {
        enum StreamingPlaybackState
        {
            Stopped,
            Playing,
            Buffering,
            Paused
        }

        private BufferedWaveProvider? bufferedWaveProvider;
        private IWavePlayer? waveOut;
        private volatile StreamingPlaybackState playbackState;
        private volatile bool fullyDownloaded;
        private long size;
        private long loaded;
        private VolumeWaveProvider16? volumeProvider;

        public Player()
        {

        }

        public void Tick()
        {
            if (playbackState != StreamingPlaybackState.Stopped)
            {
                if (waveOut == null && bufferedWaveProvider != null)
                {
                    Console.WriteLine("Creating WaveOut Device");
                    waveOut = CreateWaveOut();
                    waveOut.PlaybackStopped += OnPlaybackStopped;
                    volumeProvider = new VolumeWaveProvider16(bufferedWaveProvider);
                    volumeProvider.Volume = 1f;
                    waveOut.Init(volumeProvider);
                }
                else if (bufferedWaveProvider != null)
                {
                    var bufferedSeconds = bufferedWaveProvider.BufferedDuration.TotalSeconds;
                    Console.Write($"\rbuffered: {bufferedSeconds.ToString("0.0000")}s | downloaded: {(((float)loaded/size) * 100).ToString("0.00")}% ({loaded/1024}kb/{size/1024}kb)");
                    // make it stutter less if we buffer up a decent amount before playing
                    if (bufferedSeconds < 0.5 && playbackState == StreamingPlaybackState.Playing && !fullyDownloaded)
                    {
                        Pause();
                    }
                    else if (bufferedSeconds > 4 && playbackState == StreamingPlaybackState.Buffering)
                    {
                        Play();
                    }
                    else if (fullyDownloaded && bufferedSeconds == 0)
                    {
                        Console.WriteLine("Reached end of stream");
                        StopPlayback();
                    }
                }
            }
        }

        public async Task DoPlay(Stream stream, long size, CancellationToken cancelToken)
        {
            playbackState = StreamingPlaybackState.Buffering;
            bufferedWaveProvider = null;
            var streamLoad = Task.Run(() => StreamMp3(stream, size, cancelToken), cancelToken);

            while (playbackState != StreamingPlaybackState.Stopped && !cancelToken.IsCancellationRequested)
            {
                try
                {
                    Tick();
                    await Task.Delay(10, cancelToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            StopPlayback();
        }

        private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
        {
            Debug.WriteLine("Playback Stopped");
        }

        private void Play()
        {
            waveOut!.Play();
            Debug.WriteLine(String.Format("Started playing, waveOut.PlaybackState={0}", waveOut.PlaybackState));
            playbackState = StreamingPlaybackState.Playing;
        }

        private void Pause()
        {
            playbackState = StreamingPlaybackState.Buffering;
            waveOut!.Pause();
            Debug.WriteLine(String.Format("Paused to buffer, waveOut.PlaybackState={0}", waveOut.PlaybackState));
        }

        private void StopPlayback()
        {
            if (playbackState != StreamingPlaybackState.Stopped)
            {
                if (!fullyDownloaded)
                {
                    //webRequest.Abort();
                }

                playbackState = StreamingPlaybackState.Stopped;
                if (waveOut != null)
                {
                    waveOut.Stop();
                    waveOut.Dispose();
                    waveOut = null;
                }
            }
        }

        private IWavePlayer CreateWaveOut()
        {
            return new DirectSoundOut();
        }

        private async Task StreamMp3(Stream stream, long size, CancellationToken cancelToken)
        {
            this.size = size;
            fullyDownloaded = false;
            var buffer = new byte[16384 * 4]; // needs to be big enough to hold a decompressed frame

            IMp3FrameDecompressor? decompressor = null;
            try
            {
                var readFullyStream = new ReadFullyStream(stream, size);

                do
                {
                    this.loaded = readFullyStream.Position;
                    if (IsBufferNearlyFull())
                    {
                        // Debug.WriteLine("Buffer getting full, taking a break");
                        await Task.Delay(500, cancelToken);
                    }
                    else
                    {
                        Mp3Frame frame;
                        // var pos = stream.Position;
                        try
                        {
                            frame = Mp3Frame.LoadFromStream(readFullyStream);
                        }
                        catch (EndOfStreamException)
                        {
                            Console.WriteLine("Reached the end");
                            fullyDownloaded = true;
                            // reached the end of the MP3 file / stream
                            // break;
                            continue;
                        }
                        if (frame == null)
                        {
                            // break;
                            Console.WriteLine("No frame");
                            continue;
                        }
                        // Ignore non-Layer III frames, the frame decompressor is created with the wrong sampling rate otherwise
                        if (frame.MpegLayer != MpegLayer.Layer3)
                        {
                            continue;
                        }
                        // Debug.WriteLine($"Get frame");
                        if (decompressor == null)
                        {
                            Debug.WriteLine("Create decompressor");
                            // don't think these details matter too much - just help ACM select the right codec
                            // however, the buffered provider doesn't know what sample rate it is working at
                            // until we have a frame
                            decompressor = CreateFrameDecompressor(frame);
                            bufferedWaveProvider = new BufferedWaveProvider(decompressor.OutputFormat);
                            bufferedWaveProvider.BufferDuration =
                                TimeSpan.FromSeconds(20); // allow us to get well ahead of ourselves
                                                          //this.bufferedWaveProvider.BufferedDuration = 250;
                            bufferedWaveProvider.DiscardOnBufferOverflow = true;
                        }
                        // Console.WriteLine($"Frame: {frame.FileOffset} ({readFullyStream.Position}) - {frame.SampleRate} {frame.SampleCount} {frame.BitRate} {frame.MpegLayer} {frame.MpegVersion} {frame.ChannelMode} {frame.ChannelExtension}");
                        // Debug.WriteLine($"Decompress {bufferedWaveProvider!.BufferedDuration.TotalSeconds}");
                        int decompressed = decompressor.DecompressFrame(frame, buffer, 0);
                        // Debug.WriteLine(String.Format("Decompressed a frame {0}", decompressed));
                        bufferedWaveProvider!.AddSamples(buffer, 0, decompressed);
                    }

                } while (playbackState != StreamingPlaybackState.Stopped && !cancelToken.IsCancellationRequested);
                Console.WriteLine($"Exiting {playbackState}");
                // was doing this in a finally block, but for some reason
                // we are hanging on response stream .Dispose so never get there

                if (decompressor != null)
                {
                    decompressor.Dispose();
                }
            }
            finally
            {
                if (decompressor != null)
                {
                    decompressor.Dispose();
                }
            }
        }

        private bool IsBufferNearlyFull()
        {
            return bufferedWaveProvider != null &&
                   bufferedWaveProvider.BufferLength - bufferedWaveProvider.BufferedBytes
                   < bufferedWaveProvider.WaveFormat.AverageBytesPerSecond / 4;
        }

        private static IMp3FrameDecompressor CreateFrameDecompressor(Mp3Frame frame)
        {
            Console.WriteLine($"FORMAT: .mp3 {frame.ChannelMode} {frame.SampleRate}Hz {frame.BitRate/1024}kbps");
            WaveFormat waveFormat = new Mp3WaveFormat(frame.SampleRate, frame.ChannelMode == ChannelMode.Mono ? 1 : 2,
                frame.FrameLength, frame.BitRate);
            return new AcmMp3FrameDecompressor(waveFormat);
        }
    }
}