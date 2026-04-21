using System;
using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Resources;
using System.Windows.Threading;

namespace NetcoServerConsole
{
    internal static class AppMemoryCoordinator
    {
        private static readonly Uri StartupLogoUri = new Uri("pack://application:,,,/Assets/netco_logo_startup.png", UriKind.Absolute);
        private static int _trimSequence;

        public static BitmapSource CreateTransientStartupLogo()
        {
            return LoadBitmap(StartupLogoUri);
        }

        public static void ScheduleIdleTrim(Action? releaseUiResources = null)
        {
            var sequence = Interlocked.Increment(ref _trimSequence);
            _ = RunIdleTrimAsync(sequence, releaseUiResources);
        }

        private static async Task RunIdleTrimAsync(int sequence, Action? releaseUiResources)
        {
            try
            {
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    releaseUiResources?.Invoke();
                }, DispatcherPriority.Background);

                await Task.Delay(180).ConfigureAwait(false);

                if (sequence != Volatile.Read(ref _trimSequence))
                    return;

                await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                await Task.Run(CompactProcessMemory).ConfigureAwait(false);
            }
            catch
            {
            }
        }

        private static BitmapSource LoadBitmap(Uri resourceUri)
        {
            StreamResourceInfo? resource = Application.GetResourceStream(resourceUri);
            if (resource == null)
                throw new InvalidOperationException("No se pudo cargar el recurso de imagen.");

            using (resource.Stream)
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.PreservePixelFormat;
                bitmap.StreamSource = resource.Stream;
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
        }

        private static void CompactProcessMemory()
        {
            try
            {
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);

                using (var process = Process.GetCurrentProcess())
                {
                    try
                    {
                        EmptyWorkingSet(process.Handle);
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
        }

        [DllImport("psapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EmptyWorkingSet(IntPtr hProcess);
    }
}
