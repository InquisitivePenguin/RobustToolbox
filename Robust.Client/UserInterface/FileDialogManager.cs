using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using JetBrains.Annotations;
using Robust.Client.Interfaces.Console;
using Robust.Client.Interfaces.UserInterface;
using Robust.Shared;
using Robust.Shared.IoC;
using Robust.Shared.Log;
using Robust.Shared.Utility;

namespace Robust.Client.UserInterface
{
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    [SuppressMessage("ReSharper", "IdentifierTypo")]
    [SuppressMessage("ReSharper", "CommentTypo")]
    [SuppressMessage("ReSharper", "StringLiteralTypo")]
    internal sealed class FileDialogManager : IFileDialogManager
    {
        // Uses nativefiledialog to open the file dialogs cross platform.
        // On Linux, if the kdialog command is found, it will be used instead.
        // TODO: Should we maybe try to avoid running kdialog if the DE isn't KDE?

#if MACOS
        [Dependency] private readonly Shared.Asynchronous.ITaskManager _taskManager;
#endif

#if LINUX
        private bool _kDialogAvailable;
        private bool _checkedKDialogAvailable;
#endif

        static FileDialogManager()
        {
            DllMapHelper.RegisterSimpleMap(typeof(FileDialogManager).Assembly, "swnfd");
        }

        public async Task<string> OpenFile(FileDialogFilters filters = null)
        {
#if LINUX
            if (await IsKDialogAvailable())
            {
                return await OpenFileKDialog(filters);
            }
#endif
            return await OpenFileNfd(filters);
        }

        public async Task<string> SaveFile()
        {
#if LINUX
            if (await IsKDialogAvailable())
            {
                return await SaveFileKDialog();
            }
#endif
            return await SaveFileNfd();
        }

        public async Task<string> OpenFolder()
        {
#if LINUX
            if (await IsKDialogAvailable())
            {
                return await OpenFolderKDialog();
            }
#endif
            return await OpenFolderNfd();
        }

        private unsafe Task<string> OpenFileNfd(FileDialogFilters filters)
        {
            // Have to run it in the thread pool to avoid blocking the main thread.
            return RunAsyncMaybe(() =>
            {
                var filterPtr = IntPtr.Zero;
                byte* outPath;

                if (filters != null)
                {
                    var filterString = string.Join(';', filters.Groups.Select(f => string.Join(',', f.Extensions)));

                    filterPtr = Marshal.StringToCoTaskMemUTF8(filterString);
                }

                sw_nfdresult result;

                try
                {
                    result = sw_NFD_OpenDialog((byte*) filterPtr, null, &outPath);
                }
                finally
                {
                    if (filterPtr != IntPtr.Zero)
                    {
                        Marshal.FreeCoTaskMem(filterPtr);
                    }
                }

                return HandleNfdResult(result, outPath);
            });
        }

        private unsafe Task<string> SaveFileNfd()
        {
            // Have to run it in the thread pool to avoid blocking the main thread.
            return RunAsyncMaybe(() =>
            {
                byte* outPath;

                var result = sw_NFD_SaveDialog(null, null, &outPath);

                return HandleNfdResult(result, outPath);
            });
        }

        private unsafe Task<string> OpenFolderNfd()
        {
            // Have to run it in the thread pool to avoid blocking the main thread.
            return RunAsyncMaybe(() =>
            {
                byte* outPath;

                var result = sw_NFD_PickFolder(null, &outPath);

                return HandleNfdResult(result, outPath);
            });
        }

        // ReSharper disable once MemberCanBeMadeStatic.Local
        private Task<string> RunAsyncMaybe(Func<string> action)
        {
#if MACOS
            // macOS seems pretty annoying about having the file dialog opened from the main thread.
            // So we are forced to execute this synchronously on the main thread.
            // Also I'm calling RunOnMainThread here to provide safety in case this is ran from a different thread.
            // nativefiledialog doesn't provide any form of async API, so this WILL lock up the client.
            var tcs = new TaskCompletionSource<string>();
            _taskManager.RunOnMainThread(() => tcs.SetResult(action()));

            return tcs.Task;
#else
            // Luckily, GTK Linux and COM Windows are both happily threaded. Yay!
            return Task.Run(action);
#endif
        }

        private static unsafe string HandleNfdResult(sw_nfdresult result, byte* outPath)
        {
            switch (result)
            {
                case sw_nfdresult.SW_NFD_ERROR:
                    var errPtr = sw_NFD_GetError();
                    throw new Exception(MarshalHelper.PtrToStringUTF8(errPtr));

                case sw_nfdresult.SW_NFD_OKAY:
                    var str = MarshalHelper.PtrToStringUTF8(outPath);

                    sw_NFD_Free(outPath);
                    return str;

                case sw_nfdresult.SW_NFD_CANCEL:
                    return null;

                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

#if LINUX
        private async Task CheckKDialogSupport()
        {
            var currentDesktop = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP");
            if (currentDesktop == null || !currentDesktop.Contains("KDE"))
            {
                return;
            }

            try
            {
                var process = Process.Start(
                    new ProcessStartInfo
                    {
                        FileName = "kdialog",
                        Arguments = "--version",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false
                    });

                if (process == null)
                {
                    _kDialogAvailable = false;
                    return;
                }

                await process.WaitForExitAsync();
                _kDialogAvailable = process.ExitCode == 0;

                if (_kDialogAvailable)
                {
                    Logger.DebugS("filedialog", "kdialog available.");
                }
            }
            catch
            {
                _kDialogAvailable = false;
            }
        }

        private static Task<string> OpenFileKDialog(FileDialogFilters filters)
        {
            var sb = new StringBuilder();

            if (filters != null && filters.Groups.Count != 0)
            {
                var first = true;
                foreach (var group in filters.Groups)
                {
                    if (!first)
                    {
                        sb.Append('|');
                    }

                    foreach (var extension in group.Extensions)
                    {
                        sb.AppendFormat(".{0} ", extension);
                    }

                    sb.Append('(');

                    foreach (var extension in group.Extensions)
                    {
                        sb.AppendFormat("*.{0} ", extension);
                    }

                    sb.Append(')');

                    first = false;
                }

                sb.Append("| All Files (*)");
            }

            return RunKDialog("--getopenfilename", Environment.GetEnvironmentVariable("HOME"), sb.ToString());
        }

        private static Task<string> SaveFileKDialog()
        {
            return RunKDialog("--getsavefilename");
        }

        private static Task<string> OpenFolderKDialog()
        {
            return RunKDialog("--getexistingdirectory");
        }

        private static async Task<string> RunKDialog(params string[] options)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "kdialog",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                StandardOutputEncoding = EncodingHelpers.UTF8
            };

            foreach (var option in options)
            {
                startInfo.ArgumentList.Add(option);
            }

            var process = Process.Start(startInfo);

            DebugTools.AssertNotNull(process);

            await process.WaitForExitAsync();

            // Cancel hit.
            if (process.ExitCode == 1)
            {
                return null;
            }

            return (await process.StandardOutput.ReadLineAsync()).Trim();
        }

        private async Task<bool> IsKDialogAvailable()
        {
            if (!_checkedKDialogAvailable)
            {
                await CheckKDialogSupport();
                _checkedKDialogAvailable = true;
            }

            return _kDialogAvailable;
        }
#endif

        [DllImport("swnfd.dll")]
        private static extern unsafe byte* sw_NFD_GetError();

        [DllImport("swnfd.dll")]
        private static extern unsafe sw_nfdresult
            sw_NFD_OpenDialog(byte* filterList, byte* defaultPath, byte** outPath);

        [DllImport("swnfd.dll")]
        private static extern unsafe sw_nfdresult
            sw_NFD_SaveDialog(byte* filterList, byte* defaultPath, byte** outPath);

        [DllImport("swnfd.dll")]
        private static extern unsafe sw_nfdresult
            sw_NFD_PickFolder(byte* defaultPath, byte** outPath);

        [DllImport("swnfd.dll")]
        private static extern unsafe void sw_NFD_Free(void* ptr);

        private enum sw_nfdresult
        {
            SW_NFD_ERROR,
            SW_NFD_OKAY,
            SW_NFD_CANCEL,
        }
    }

    [UsedImplicitly]
    internal sealed class TestOpenFileCommand : IConsoleCommand
    {
        // ReSharper disable once StringLiteralTypo
        public string Command => "testopenfile";
        public string Description => string.Empty;
        public string Help => string.Empty;

        public bool Execute(IDebugConsole console, params string[] args)
        {
            Inner(console);
            return false;
        }

        private static async void Inner(IDebugConsole console)
        {
            var manager = IoCManager.Resolve<IFileDialogManager>();
            var path = await manager.OpenFile();

            console.AddLine(path ?? string.Empty);
        }
    }
}
