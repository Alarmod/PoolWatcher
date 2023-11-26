//#define kill_msi_after_exec
//#define TOPMOST

using CommandLine;
using IWshRuntimeLibrary;
using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace PoolWatcher
{
 public static class Extensions
 {
  public static string Filter(this string str, List<char> charsToRemove)
  {
   foreach (char c in charsToRemove)
   {
    str = str.Replace(c.ToString(), String.Empty);
   }

   return str;
  }

  public static string Filter(this string str, List<string> stringsToRemove)
  {
   foreach (string s in stringsToRemove)
   {
    str = str.Replace(s, String.Empty);
   }

   return str;
  }
 }

 public static class ConsoleWindow
 {
  public static class NativeFunctions
  {
   [DllImport("user32.dll", SetLastError = true)]
   [return: MarshalAs(UnmanagedType.Bool)]
   public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, int uFlags);

   public enum StdHandle : int
   {
    STD_INPUT_HANDLE = -10,
    STD_OUTPUT_HANDLE = -11,
    STD_ERROR_HANDLE = -12,
   }

   [DllImport("kernel32.dll", SetLastError = true)]
   public static extern IntPtr GetStdHandle(int nStdHandle); //returns Handle

   public enum ConsoleMode : uint
   {
    ENABLE_ECHO_INPUT = 0x0004,
    ENABLE_EXTENDED_FLAGS = 0x0080,
    ENABLE_INSERT_MODE = 0x0020,
    ENABLE_LINE_INPUT = 0x0002,
    ENABLE_MOUSE_INPUT = 0x0010,
    ENABLE_PROCESSED_INPUT = 0x0001,
    ENABLE_QUICK_EDIT_MODE = 0x0040,
    ENABLE_WINDOW_INPUT = 0x0008,
    ENABLE_VIRTUAL_TERMINAL_INPUT = 0x0200,

    //screen buffer handle
    ENABLE_PROCESSED_OUTPUT = 0x0001,
    ENABLE_WRAP_AT_EOL_OUTPUT = 0x0002,
    ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004,
    DISABLE_NEWLINE_AUTO_RETURN = 0x0008,
    ENABLE_LVB_GRID_WORLDWIDE = 0x0010
   }

   [DllImport("kernel32.dll", SetLastError = true)]
   public static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

   [DllImport("kernel32.dll", SetLastError = true)]
   public static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);
  }

  public static void QuickEditMode(bool Enable)
  {
   //QuickEdit lets the user select text in the console window with the mouse, to copy to the windows clipboard.
   //But selecting text stops the console process (e.g. unzipping). This may not be always wanted.
   IntPtr consoleHandle = NativeFunctions.GetStdHandle((int)NativeFunctions.StdHandle.STD_INPUT_HANDLE);
   UInt32 consoleMode;

   NativeFunctions.GetConsoleMode(consoleHandle, out consoleMode);
   if (Enable)
    consoleMode |= ((uint)NativeFunctions.ConsoleMode.ENABLE_QUICK_EDIT_MODE);
   else
    consoleMode &= ~((uint)NativeFunctions.ConsoleMode.ENABLE_QUICK_EDIT_MODE);

   consoleMode |= ((uint)NativeFunctions.ConsoleMode.ENABLE_EXTENDED_FLAGS);

   NativeFunctions.SetConsoleMode(consoleHandle, consoleMode);
  }
 }

 class Options
 {
  public const int default_wait_timeout_value = 360;
  public const int default_share_wait_timeout_value = 1200;

  public const int default_sleep_timeout = 90000;

  [Option('k', "kill_pill", Default = 0, Required = false)]
  public int kill_pill { get; set; }

  [Option('w', "without_external_windows", Default = 1, Required = false)]
  public int without_external_windows { get; set; }

  [Option('s', "with_antiwatchdog", Default = 1, Required = false)]
  public int with_antiwatchdog { get; set; }

  [Option('o', "direct_order", Default = 1, Required = false)]
  public int direct_order { get; set; }

  [Option('e', "exit_after_miner_fail", Default = 1, Required = false)]
  public int exit_after_miner_fail { get; set; }

  [Option('d', "use_dummy_miner", Default = 0, Required = false)]
  public int use_dummy_miner { get; set; }

  [Option('p', "wait_timeout", Default = default_wait_timeout_value, Required = false)]
  public int wait_timeout { get; set; }

  [Option('q', "share_wait_timeout", Default = default_share_wait_timeout_value, Required = false)]
  public int share_wait_timeout { get; set; }

  [Option('i', "ignore_no_active_pools_message", Default = 1, Required = false)]
  public int ignore_no_active_pools_message { get; set; }

  [Option('v', "ban_timeout", Default = 30, Required = false)]
  public int ban_timeout { get; set; }

  [Option('m', "ban_or_restart_no_shares_event", Default = 1, Required = false)]
  public int ban_or_restart_no_shares_event { get; set; }
 }

 [SecurityCritical]
 public static class ProcessHelpers
 {
  public static bool IsRunning(string name) => Process.GetProcessesByName(name).Length > 0;
 }

 [SecurityCritical]
 public static class ArrayExtensions
 {
  public static int IndexOf<T>(this T[] array, T value)
  {
   return Array.IndexOf(array, value);
  }
 }

 [SecurityCritical]
 public static class Program
 {
  static readonly int sleep_default_timeout = 500; // базовая единица ожидания

  static volatile Object _lockObj = new Object();

  // Основная монета
  static readonly string defaultPool = "vds.666pool.cn";
  static readonly int defaultPort = 9338;
  static readonly string default_dummy_params = "--algo vds --server " + defaultPool + " --port " + defaultPort + " --user VcXGox4tgyfGP1qkPcrCqzxpGSvpEZzhP1X@pps.FARM --pass x --pec 0 --watchdog 0";

  /// <summary>
  /// Converts a wmi date into a proper date
  /// </summary>
  /// <param jobName="wmiDate">Wmi formatted date</param>
  /// <returns>Date time object</returns>
  private static bool ConvertFromWmiDate(string wmiDate, out DateTime properDate)
  {
   properDate = DateTime.MinValue;

   string properDateString;

   if (String.IsNullOrEmpty(wmiDate)) return false;

   wmiDate = wmiDate.Trim().ToLower(CultureInfo.CurrentCulture).Replace("*", "0");

   string[] months = new string[] { "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" };

   try
   {
    properDateString = String.Format(null, "{0}-{1}-{2} {3}:{4}:{5}.{6}",
    wmiDate.Substring(6, 2),
    months[int.Parse(wmiDate.Substring(4, 2), null) - 1],
    wmiDate.Substring(0, 4),
    wmiDate.Substring(8, 2),
    wmiDate.Substring(10, 2),
    wmiDate.Substring(12, 2),
    wmiDate.Substring(15, 6));
   }
   catch (InvalidCastException) { return false; }
   catch (ArgumentOutOfRangeException) { return false; }

   if (!DateTime.TryParse(properDateString, out properDate)) return false;

   return true;
  }

  static UInt64 masterMinerProcessCounter, slave0MinerProcessCounter, slave1MinerProcessCounter;

  [DllImport("kernel32.dll")]
  static extern ErrorModes SetErrorMode(ErrorModes uMode);

  [DllImport("kernel32.dll", SetLastError = true)]
  static extern bool SetThreadErrorMode(ErrorModes dwNewMode, out ErrorModes lpOldMode);

  [DllImport("wer.dll", SetLastError = true, CharSet = CharSet.Unicode)]
  static extern int WerAddExcludedApplication(String pwzExeName, bool bAllUsers);

  [Flags]
  enum ErrorModes : uint
  {
   SYSTEM_DEFAULT = 0x0,
   SEM_FAILCRITICALERRORS = 0x0001,
   SEM_NOALIGNMENTFAULTEXCEPT = 0x0004,
   SEM_NOGPFAULTERRORBOX = 0x0002,
   SEM_NOOPENFILEERRORBOX = 0x8000,
   SEM_NONE = SEM_FAILCRITICALERRORS | SEM_NOALIGNMENTFAULTEXCEPT | SEM_NOGPFAULTERRORBOX | SEM_NOOPENFILEERRORBOX
  }

  [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)]
  static extern bool CreateProcessAsUser(
      IntPtr hToken,
      string lpApplicationName,
      string lpCommandLine,
      ref SECURITY_ATTRIBUTES lpProcessAttributes,
      ref SECURITY_ATTRIBUTES lpThreadAttributes,
      bool bInheritHandles,
      uint dwCreationFlags,
      IntPtr lpEnvironment,
      string lpCurrentDirectory,
      ref STARTUPINFO lpStartupInfo,
      out PROCESS_INFORMATION lpProcessInformation);

  [Flags]
  enum CreateProcessFlags : uint
  {
   DEBUG_PROCESS = 0x00000001,
   DEBUG_ONLY_THIS_PROCESS = 0x00000002,
   CREATE_SUSPENDED = 0x00000004,
   DETACHED_PROCESS = 0x00000008,
   CREATE_NEW_CONSOLE = 0x00000010,
   NORMAL_PRIORITY_CLASS = 0x00000020,
   IDLE_PRIORITY_CLASS = 0x00000040,
   HIGH_PRIORITY_CLASS = 0x00000080,
   REALTIME_PRIORITY_CLASS = 0x00000100,
   CREATE_NEW_PROCESS_GROUP = 0x00000200,
   CREATE_UNICODE_ENVIRONMENT = 0x00000400,
   CREATE_SEPARATE_WOW_VDM = 0x00000800,
   CREATE_SHARED_WOW_VDM = 0x00001000,
   CREATE_FORCEDOS = 0x00002000,
   BELOW_NORMAL_PRIORITY_CLASS = 0x00004000,
   ABOVE_NORMAL_PRIORITY_CLASS = 0x00008000,
   INHERIT_PARENT_AFFINITY = 0x00010000,
   INHERIT_CALLER_PRIORITY = 0x00020000,
   CREATE_PROTECTED_PROCESS = 0x00040000,
   EXTENDED_STARTUPINFO_PRESENT = 0x00080000,
   PROCESS_MODE_BACKGROUND_BEGIN = 0x00100000,
   PROCESS_MODE_BACKGROUND_END = 0x00200000,
   CREATE_BREAKAWAY_FROM_JOB = 0x01000000,
   CREATE_PRESERVE_CODE_AUTHZ_LEVEL = 0x02000000,
   CREATE_DEFAULT_ERROR_MODE = 0x04000000,
   CREATE_NO_WINDOW = 0x08000000,
   PROFILE_USER = 0x10000000,
   PROFILE_KERNEL = 0x20000000,
   PROFILE_SERVER = 0x40000000,
   CREATE_IGNORE_SYSTEM_DEFAULT = 0x80000000,
  }

  [DllImport("kernel32.dll")]
  static extern bool CreatePipe(out IntPtr hReadPipe, out IntPtr hWritePipe, ref SECURITY_ATTRIBUTES lpPipeAttributes, uint nSize);

  [DllImport("kernel32.dll", SetLastError = true)]
  static extern bool SetHandleInformation(IntPtr hObject, HANDLE_FLAGS dwMask, HANDLE_FLAGS dwFlags);

  [Flags]
  enum HANDLE_FLAGS : uint
  {
   None = 0,
   INHERIT = 1,
   PROTECT_FROM_CLOSE = 2
  }

  /// <summary>
  /// Determines whether certain StartUpInfo members are used when the process creates a window.
  /// </summary>
  [FlagsAttribute]
  enum StartUpInfoFlags : uint
  {
   /// <summary>
   /// If this value is not specified, the wShowWindow member is ignored.
   /// </summary>
   UseShowWindow = 0x0000001,
   /// <summary>
   /// If this value is not specified, the dwXSize and dwYSize members are ignored.
   /// </summary>
   UseSize = 0x00000002,
   /// <summary>
   /// If this value is not specified, the dwX and dwY members are ignored.
   /// </summary>
   UsePosition = 0x00000004,
   /// <summary>
   /// If this value is not specified, the dwXCountChars and dwYCountChars members are ignored.
   /// </summary>
   UseCountChars = 0x00000008,
   /// <summary>
   /// If this value is not specified, the dwFillAttribute member is ignored.
   /// </summary>
   UseFillAttribute = 0x00000010,
   /// <summary>
   /// Indicates that the process should be run in full-screen mode, rather than in windowed mode.
   /// </summary>
   RunFullScreen = 0x00000020,
   /// <summary>
   /// Indicates that the cursor is in feedback mode after CreateProcess is called. The system turns the feedback cursor off after the first call to GetMessage.
   /// </summary>
   ForceOnFeedback = 0x00000040,
   /// <summary>
   /// Indicates that the feedback cursor is forced off while the process is starting. The Normal Select cursor is displayed.
   /// </summary>
   ForceOffFeedback = 0x00000080,
   /// <summary>
   /// Sets the standard input, standard output, and standard error handles for the process to the handles specified in the hStdInput, hStdOutput, and hStdError members of the StartUpInfo structure. If this value is not specified, the hStdInput, hStdOutput, and hStdError members of the STARTUPINFO structure are ignored.
   /// </summary>
   UseStandardHandles = 0x00000100,
   /// <summary>
   /// When this flag is specified, the hStdInput member is to be used as the hotkey value instead of the standard-input pipe.
   /// </summary>
   UseHotKey = 0x00000200,
   /// <summary>
   /// When this flag is specified, the StartUpInfo's hStdOutput member is used to specify a handle to a monitor, on which to start the new process. This monitor handle can be obtained by any of the multiple-monitor display functions (i.e. EnumDisplayMonitors, MonitorFromPoint, MonitorFromWindow, etc...).
   /// </summary>
   UseMonitor = 0x00000400,
   /// <summary>
   /// Use the HICON specified in the hStdOutput member (incompatible with UseMonitor).
   /// </summary>
   UseIcon = 0x00000400,
   /// <summary>
   /// Program was started through a shortcut. The lpTitle contains the shortcut path.
   /// </summary>
   TitleShortcut = 0x00000800,
   /// <summary>
   /// The process starts with normal priority. After the first call to GetMessage, the priority is lowered to idle.
   /// </summary>
   Screensaver = 0x08000000
  }

  [StructLayout(LayoutKind.Sequential)]
  struct SECURITY_ATTRIBUTES
  {
   public int nLength;
   public IntPtr lpSecurityDescriptor;
   public int bInheritHandle;
  }

  [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
  struct STARTUPINFO
  {
   public Int32 cb;
   public string lpReserved;
   public string lpDesktop;
   public string lpTitle;
   public Int32 dwX;
   public Int32 dwY;
   public Int32 dwXSize;
   public Int32 dwYSize;
   public Int32 dwXCountChars;
   public Int32 dwYCountChars;
   public Int32 dwFillAttribute;
   public Int32 dwFlags;
   public Int16 wShowWindow;
   public Int16 cbReserved2;
   public IntPtr lpReserved2;
   public IntPtr hStdInput;
   public IntPtr hStdOutput;
   public IntPtr hStdError;
  }

  [StructLayout(LayoutKind.Sequential)]
  internal struct PROCESS_INFORMATION
  {
   public IntPtr hProcess;
   public IntPtr hThread;
   public int dwProcessId;
   public int dwThreadId;
  }

  [DllImport("kernel32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  static extern bool CloseHandle(IntPtr hObject);

  [Flags]
  enum ProcessAccessFlags : uint
  {
   All = 0x001F0FFF,
   Terminate = 0x00000001,
   CreateThread = 0x00000002,
   VirtualMemoryOperation = 0x00000008,
   VirtualMemoryRead = 0x00000010,
   VirtualMemoryWrite = 0x00000020,
   DuplicateHandle = 0x00000040,
   CreateProcess = 0x000000080,
   SetQuota = 0x00000100,
   SetInformation = 0x00000200,
   QueryInformation = 0x00000400,
   QueryLimitedInformation = 0x00001000,
   Synchronize = 0x00100000
  }

  [DllImport("user32.dll")]
  static extern int DeleteMenu(IntPtr hMenu, int nPosition, int wFlags);

  const int MF_BYPOSITION = 0x400;

  [DllImport("User32")]
  private static extern int RemoveMenu(IntPtr hMenu, int nPosition, int wFlags);

  [DllImport("User32")]
  private static extern IntPtr GetSystemMenu(IntPtr hWnd, bool bRevert);

  [DllImport("User32")]
  private static extern int GetMenuItemCount(IntPtr hWnd);

  [DllImport("kernel32.dll", ExactSpelling = true)]
  static extern IntPtr GetConsoleWindow();

  enum ProcessVariant
  {
   Master,
   Slave0,
   Slave1
  }

  [HandleProcessCorruptedStateExceptions, SecurityCritical]
  static void CallProcess(bool isBatFile, string commandLine, string workingDirectory, CreateProcessFlags creationFlags, ProcessVariant pvar, bool redirect_input)
  {
   try
   {
    SafeFileHandle shStdOutRead = null, shStdErrRead = null;
    SafeFileHandle shStdInWrite = null;
    StreamReader readerStdOut = null, readerStdErr = null;
    StreamWriter writerStdIn = null;

    var hToken = WindowsIdentity.GetCurrent().Token;
    if (hToken == IntPtr.Zero)
    {
     if (Program.ru_lang)
      throw new InvalidOperationException("Токен аутентификации не был установлен с LogonUser");
     else
      throw new InvalidOperationException("Authentication Token has not been set with LogonUser");
    }

    SECURITY_ATTRIBUTES sa = new SECURITY_ATTRIBUTES();
    sa.nLength = Marshal.SizeOf(sa);
    sa.lpSecurityDescriptor = IntPtr.Zero;
    sa.bInheritHandle = 1;

    STARTUPINFO startupInfo = new STARTUPINFO();
    {
     startupInfo.cb = Marshal.SizeOf(startupInfo);

     if (options.without_external_windows == 1)
     {
      bool success;

      // OUT     
      success = CreatePipe(out IntPtr hHandle, out IntPtr hChildHandle, ref sa, 0);
      if (!success) throw new System.ComponentModel.Win32Exception();

      startupInfo.hStdOutput = hChildHandle;

      success = SetHandleInformation(hHandle, HANDLE_FLAGS.INHERIT, 0);
      if (!success) throw new System.ComponentModel.Win32Exception();

      shStdOutRead = new SafeFileHandle(hHandle, true);
      FileStream fs = new FileStream(shStdOutRead, FileAccess.Read);
      readerStdOut = new StreamReader(fs);

      // ERR
      success = CreatePipe(out hHandle, out hChildHandle, ref sa, 0);
      if (!success) throw new System.ComponentModel.Win32Exception();

      startupInfo.hStdError = hChildHandle;

      success = SetHandleInformation(hHandle, HANDLE_FLAGS.INHERIT, 0);
      if (!success) throw new System.ComponentModel.Win32Exception();

      shStdErrRead = new SafeFileHandle(hHandle, true);
      fs = new FileStream(shStdErrRead, FileAccess.Read);
      readerStdErr = new StreamReader(fs);

      if (redirect_input)
      {
       // IN
       success = CreatePipe(out hChildHandle, out hHandle, ref sa, 0);

       if (!success) throw new System.ComponentModel.Win32Exception();

       startupInfo.hStdInput = hChildHandle;

       success = SetHandleInformation(hHandle, HANDLE_FLAGS.INHERIT, 0);
       if (!success) throw new System.ComponentModel.Win32Exception();

       shStdInWrite = new SafeFileHandle(hHandle, true);
       fs = new FileStream(shStdInWrite, FileAccess.Write);
       writerStdIn = new StreamWriter(fs);
      }
     }

     startupInfo.dwFlags = (int)StartUpInfoFlags.UseStandardHandles;
    }

    if (isBatFile)
    {
     if (Program.ru_lang)
      Console.WriteLine("Запуск майнера должен быть выполнен не позднее чем через " + options.wait_timeout + ".000 секунд; если Ваш батник не сразу запускает процесс добычи, а Вы остановите батник тем или иным способом, то процесс прервется после соответствующего таймаута, не нервничайте, будьте счастливы");
     else
      Console.WriteLine("Miner launch must be completed no later than in " + options.wait_timeout + ".000 seconds; if Your bat-file does not immediately start the mining process, and You stop bat-file in one way or another, the process will be interrupted after a corresponding timeout, don't worry be happy");
    }

    DateTime dummyDate = DateTime.Now;
    bool result = CreateProcessAsUser(hToken, null, commandLine, ref sa, ref sa, true, (uint)creationFlags, IntPtr.Zero, workingDirectory, ref startupInfo, out PROCESS_INFORMATION processInfo);
    if (result == false) throw new System.ComponentModel.Win32Exception();

    DateTime timeOfStart;
    Process curr_process = null;

    try
    {
     curr_process = Process.GetProcessById(processInfo.dwProcessId);

     timeOfStart = curr_process.StartTime;
    }
    catch
    {
     timeOfStart = dummyDate;
    }

    switch (pvar)
    {
     case ProcessVariant.Master:
      {
       masterMinerProcessStartTime = timeOfStart;

       break;
      }
     case ProcessVariant.Slave0:
      {
       slaveMinerProcess0StartTime = timeOfStart;

       break;
      }
     case ProcessVariant.Slave1:
      {
       slaveMinerProcess1StartTime = timeOfStart;

       break;
      }
    }

    switch (pvar)
    {
     case ProcessVariant.Master:
      {
       masterMinerProcess = curr_process;
       masterMinerProcessId = processInfo.dwProcessId;

       break;
      }
     case ProcessVariant.Slave0:
      {
       slaveMinerProcess0 = curr_process;
       slaveMinerProcess0Id = processInfo.dwProcessId;

       break;
      }
     case ProcessVariant.Slave1:
      {
       slaveMinerProcess1 = curr_process;
       slaveMinerProcess1Id = processInfo.dwProcessId;

       break;
      }
    }

    // thread monitoring
    {
     if (options.without_external_windows == 1)
     {
      if (redirect_input) CloseHandle(startupInfo.hStdInput);
      CloseHandle(startupInfo.hStdError);
      CloseHandle(startupInfo.hStdOutput);

      if (redirect_input)
      {
       //writerStdIn.WriteLine("dir");

       writerStdIn.Close();
       if (shStdInWrite.IsClosed == false)
        shStdInWrite.Close();
      }
     }

     Thread outThread = null;
     Thread errThread = null;

     if (options.without_external_windows == 1)
     {
      outThread = new Thread(() =>
      {
       DateTime thead_dt = DateTime.Now;

       try
       {
        while (readerStdOut.BaseStream.CanRead)
        {
         string output = String.Empty;

         Task<string> t = readerStdOut.ReadLineAsync();

         bool global_break = false;
         while (true)
         {
          DateTime dt = DateTime.Now;
          switch (pvar)
          {
           case ProcessVariant.Master:
            {
             dt = masterMinerProcessStartTime;

             break;
            }
           case ProcessVariant.Slave0:
            {
             dt = slaveMinerProcess0StartTime;

             break;
            }
           case ProcessVariant.Slave1:
            {
             dt = slaveMinerProcess1StartTime;

             break;
            }
          }

          bool t_wait_result = false;
          for (int i = 0; i < 100; i++)
          {
           t_wait_result = t.Wait(3000);

           if ((dt - thead_dt).TotalMilliseconds > 2500)
           {
            t_wait_result = false;
            global_break = true;
            Console.WriteLine("OUT Exit 0");
            break;
           }
           else if (curr_process == null)
           {
            t_wait_result = false;
            global_break = true;
            Console.WriteLine("OUT Exit 1");
            break;
           }
           else
           {
            try
            {
             if (curr_process.HasExited)
             {
              t_wait_result = false;
              global_break = true;
              Console.WriteLine("OUT Exit 2");
              break;
             }
            }
            catch
            {
             t_wait_result = false;
             global_break = true;
             Console.WriteLine("OUT Exit 3");
             break;
            }
           }

           if (t_wait_result == true) break;
          }

          if (global_break) break;

          if (t_wait_result)
          {
           output = t.Result;

           if (!String.IsNullOrEmpty(output))
           {
            lock (lobj)
            {
             ParseMessage(curr_process, output);

             break;
            }
           }
           else
           {
            Thread.Sleep(1000);
           }
          }
          else
          {
           lock (lobj)
           {
            if (Program.ru_lang)
             Console.WriteLine("Контроль OUT-потока майнера продолжается");
            else
             Console.WriteLine("OUT-thread control online");
           }
          }
         }

         if (global_break)
         {
          lock (lobj)
          {
           if (Program.ru_lang)
            Console.WriteLine("Контроль OUT-потока майнера завершен");
           else
            Console.WriteLine("OUT-thread control finished");
          }
          break;
         }
        }

        readerStdOut.Close();
        if (shStdOutRead.IsClosed == false)
        {
         shStdOutRead.Close();
        }
       }
       catch (Exception ex)
       {
        lock (lobj)
        {
         Console.WriteLine(ex.Message + Environment.NewLine + ex.StackTrace);

         if (Program.ru_lang)
          Console.WriteLine("Убиваем зависший процесс");
         else
          Console.WriteLine("Kill hung process");
        }

        criticalEvent(curr_process);
       }
      });

      errThread = new Thread(() =>
      {
       DateTime thead_dt = DateTime.Now;

       try
       {
        while (readerStdErr.BaseStream.CanRead)
        {
         string output = String.Empty;

         Task<string> t = readerStdErr.ReadLineAsync();

         bool global_break = false;
         while (true)
         {
          DateTime dt = DateTime.Now;
          switch (pvar)
          {
           case ProcessVariant.Master:
            {
             dt = masterMinerProcessStartTime;

             break;
            }
           case ProcessVariant.Slave0:
            {
             dt = slaveMinerProcess0StartTime;

             break;
            }
           case ProcessVariant.Slave1:
            {
             dt = slaveMinerProcess1StartTime;

             break;
            }
          }

          bool t_wait_result = false;
          for (int i = 0; i < 100; i++)
          {
           t_wait_result = t.Wait(3000);

           if ((dt - thead_dt).TotalMilliseconds > 2500)
           {
            t_wait_result = false;
            global_break = true;
            Console.WriteLine("ERR Exit 0");
            break;
           }
           else if (curr_process == null)
           {
            t_wait_result = false;
            global_break = true;
            Console.WriteLine("ERR Exit 1");
            break;
           }
           else
           {
            try
            {
             if (curr_process.HasExited)
             {
              t_wait_result = false;
              global_break = true;
              Console.WriteLine("ERR Exit 2");
              break;
             }
            }
            catch
            {
             t_wait_result = false;
             global_break = true;
             Console.WriteLine("ERR Exit 3");
             break;
            }
           }

           if (t_wait_result == true) break;
          }

          if (global_break) break;

          if (t_wait_result)
          {
           output = t.Result;

           if (!String.IsNullOrEmpty(output))
           {
            lock (lobj)
            {
             ParseMessage(curr_process, output);

             break;
            }
           }
           else
           {
            Thread.Sleep(1000);
           }
          }
          else
          {
           lock (lobj)
           {
            if (Program.ru_lang)
             Console.WriteLine("Контроль CERR-потока майнера продолжается");
            else
             Console.WriteLine("CERR-thread control online");
           }
          }
         }

         if (global_break)
         {
          lock (lobj)
          {
           if (Program.ru_lang)
            Console.WriteLine("Контроль CERR-потока майнера завершен");
           else
            Console.WriteLine("CERR-thread control finished");
          }
          break;
         }
        }

        readerStdErr.Close();
        if (shStdErrRead.IsClosed == false)
         shStdErrRead.Close();
       }
       catch (Exception ex)
       {
        lock (lobj)
        {
         Console.WriteLine(ex.Message + Environment.NewLine + ex.StackTrace);

         if (Program.ru_lang)
          Console.WriteLine("Убиваем зависший процесс");
         else
          Console.WriteLine("Kill hung process");
        }

        criticalEvent(curr_process);
       }
      });
     }

     Thread asyncThread = null;

     if (isBatFile)
     {
      asyncThread = new Thread(() =>
      {
       List<DateTime> catched_childrens_of_miners_first_seen = new List<DateTime>();
       List<DateTime> catched_childrens_of_miners_last_update = new List<DateTime>();

       List<int> catched_miners = new List<int>();
       List<DateTime> catched_miners_start_datetime = new List<DateTime>();

       List<List<int>> childrens_of_catched_miners = new List<List<int>>();

       bool fastThreadExit = false;
       DateTime dt_now = DateTime.Now;
       while (true)
       {
        if (options.without_external_windows == 1 && outThread.IsAlive == false && errThread.IsAlive == false)
        {
         fastThreadExit = true;

         break;
        }
        else
        {
         UInt32 minersProcessesCounter = 0;

         ManagementObjectSearcher searcher = new ManagementObjectSearcher(
  "SELECT * " +
  "FROM Win32_Process " +
  "WHERE ParentProcessId=" + processInfo.dwProcessId);

         ManagementObjectCollection collection = searcher.Get();

         if (collection.Count > 0)
         {
          foreach (var item in collection)
          {
           if (ConvertFromWmiDate((string)item["CreationDate"], out DateTime createDate))
           {
            if (DateTime.Compare(timeOfStart, createDate) <= 0)
            {
             try
             {
              string ProcessName = Path.GetFileNameWithoutExtension((string)item["Name"]);

              if (ProcessName != "OhGodAnETHlargementPill-r2" && ProcessName != "sleep" && ProcessName != "timeout" && ProcessName != "conhost" && ProcessName != "MSIAfterburner" && ProcessName != "curl" && ProcessName != "tasklist" && ProcessName != "find" && ProcessName != "powershell" && ProcessName != "start" && ProcessName != "cd" && ProcessName != "taskkill")
              {
               if (minersProcessesCounter == 0)
               {
                if (Program.ru_lang)
                 Console.WriteLine(Environment.NewLine + "Выявлено начало добычи" + Environment.NewLine);
                else
                 Console.WriteLine(Environment.NewLine + "Catched mining start" + Environment.NewLine);

                if (mainThread_enabled)
                {
                 lock (timeOfLatestAccepted_SyncObject)
                 {
                  timeOfLatestAccepted = (object)DateTime.Now;

                  acceptedTimeOut = (object)(options.share_wait_timeout + 360);

                  Console.WriteLine("Инициализировали слежение за шарами, первую шару ждем {0}.000 секунд; следующие шары на 360 секунд меньше", ((int)acceptedTimeOut).ToString());
                 }
                }
               }

               UInt32 childProcessId = (UInt32)item["ProcessId"];

               List<int> childrens = new List<int>();
               ManagementObjectSearcher mos = new ManagementObjectSearcher(String.Format(null, "Select * From Win32_Process Where ParentProcessID={0}", childProcessId));

               var mos_get = mos.Get();
               DateTime first_seen = DateTime.Now;
               DateTime last_update = DateTime.MinValue;

               foreach (ManagementObject mo in mos_get)
               {
                if (ConvertFromWmiDate((string)mo["CreationDate"], out DateTime createAsyncDate))
                {
                 if (DateTime.Compare(timeOfStart, createAsyncDate) <= 0)
                 {
                  childrens.Add((int)((UInt32)mo["ProcessID"]));

                  if (DateTime.Compare(first_seen, createAsyncDate) > 0) first_seen = createAsyncDate;
                  if (DateTime.Compare(last_update, createAsyncDate) < 0) last_update = createAsyncDate;
                 }
                }
               }
               mos.Dispose();

               Console.WriteLine("Add process into database: pid - {0}, name - {1}", childProcessId, ProcessName);
               {
                ManagementObjectSearcher searcher_subprocesses = new ManagementObjectSearcher("SELECT * FROM Win32_Process WHERE ParentProcessId=" + childProcessId);

                ManagementObjectCollection collection_subprocesses = searcher_subprocesses.Get();

                if (collection_subprocesses.Count > 0)
                {
                 string tmp_output = "Процесс с идентификатором " + childProcessId + " на старте имеет следующие подпроцессы: ";
                 foreach (var item_subprocesses in collection_subprocesses)
                 {
                  string ProcessName_subprocesses = Path.GetFileNameWithoutExtension((string)item_subprocesses["Name"]);

                  tmp_output += ("'" + ProcessName_subprocesses + "' ");
                 }
                 Console.WriteLine(tmp_output);
                }
               }

               catched_miners.Add((int)childProcessId);
               catched_miners_start_datetime.Add(createDate);

               childrens_of_catched_miners.Add(childrens);

               catched_childrens_of_miners_first_seen.Add(first_seen);

               if (childrens.Count > 0)
                catched_childrens_of_miners_last_update.Add(last_update);
               else
                catched_childrens_of_miners_last_update.Add(DateTime.Now);

               minersProcessesCounter++;
              }
             }
             catch { }
            }
           }
          }
         }

         searcher.Dispose();

         if (minersProcessesCounter > 0) break;
        }

        Thread.Sleep(sleep_default_timeout);

        if ((DateTime.Now - dt_now).TotalMilliseconds >= (options.wait_timeout * 1000))
        {
         if (Program.ru_lang)
          Console.WriteLine("Превышено время ожидания начала добычи");
         else
          Console.WriteLine("Exceeded waiting time for mining start");

         break;
        }
       }

       if (fastThreadExit == false)
       {
        DateTime exit_timer = DateTime.Now;

        bool exitShutdown = false;
        UInt64 exitShutdownCounter = 0;

        while (true)
        {
         if (options.without_external_windows == 1 && outThread.IsAlive == false && errThread.IsAlive == false)
         {
          break;
         }
         else
         {
          List<int> indexesOfUnusedProcesses = new List<int>();
          for (int pindex = 0; pindex < catched_miners.Count; pindex++)
           indexesOfUnusedProcesses.Add(pindex);

          UInt32 minersProcessesCounter = 0;
          ManagementObjectSearcher searcher = new ManagementObjectSearcher(
  "SELECT * " +
  "FROM Win32_Process " +
  "WHERE ParentProcessId=" + processInfo.dwProcessId);

          ManagementObjectCollection collection = searcher.Get();

          if (collection.Count > 0)
          {
           foreach (var item in collection)
           {
            if (ConvertFromWmiDate((string)item["CreationDate"], out DateTime createDate))
            {
             if (DateTime.Compare(timeOfStart, createDate) <= 0)
             {
              try
              {
               string ProcessName = Path.GetFileNameWithoutExtension((string)item["Name"]);

               if (ProcessName != "OhGodAnETHlargementPill-r2" && ProcessName != "sleep" && ProcessName != "timeout" && ProcessName != "conhost" && ProcessName != "MSIAfterburner" && ProcessName != "curl" && ProcessName != "tasklist" && ProcessName != "find" && ProcessName != "powershell" && ProcessName != "start" && ProcessName != "cd" && ProcessName != "taskkill")
               {
                UInt32 childProcessId = (UInt32)item["ProcessId"];

                bool old_process_founded = false;
                for (int pid_index = 0; pid_index < catched_miners.Count; pid_index++)
                {
                 if (catched_miners[pid_index] == childProcessId)
                 {
                  if (DateTime.Compare(createDate, catched_miners_start_datetime[pid_index]) == 0)
                  {
                   List<int> childrens = new List<int>();
                   ManagementObjectSearcher mos = new ManagementObjectSearcher(String.Format(null, "Select * From Win32_Process Where ParentProcessID={0}", childProcessId));

                   var mos_get = mos.Get();
                   DateTime first_seen = DateTime.Now;
                   DateTime last_update = DateTime.MinValue;

                   foreach (ManagementObject mo in mos_get)
                   {
                    if (ConvertFromWmiDate((string)mo["CreationDate"], out DateTime createAsyncDate))
                    {
                     if (DateTime.Compare(timeOfStart, createAsyncDate) <= 0)
                     {
                      childrens.Add((int)((UInt32)mo["ProcessID"]));

                      if (DateTime.Compare(first_seen, createAsyncDate) > 0) first_seen = createAsyncDate;
                      if (DateTime.Compare(last_update, createAsyncDate) < 0) last_update = createAsyncDate;
                     }
                    }
                   }
                   mos.Dispose();

                   childrens_of_catched_miners[pid_index] = childrens;

                   catched_childrens_of_miners_first_seen[pid_index] = first_seen;

                   if (childrens.Count > 0)
                    catched_childrens_of_miners_last_update[pid_index] = last_update;
                   else
                    catched_childrens_of_miners_last_update[pid_index] = DateTime.Now;

                   indexesOfUnusedProcesses.Remove(pid_index);

                   old_process_founded = true;

                   break;
                  }
                 }
                }

                if (old_process_founded == false)
                {
                 List<int> childrens = new List<int>();
                 ManagementObjectSearcher mos = new ManagementObjectSearcher(String.Format(null, "Select * From Win32_Process Where ParentProcessID={0}", childProcessId));

                 var mos_get = mos.Get();
                 DateTime first_seen = DateTime.Now;
                 DateTime last_update = DateTime.MinValue;

                 foreach (ManagementObject mo in mos_get)
                 {
                  if (ConvertFromWmiDate((string)mo["CreationDate"], out DateTime createAsyncDate))
                  {
                   if (DateTime.Compare(timeOfStart, createAsyncDate) <= 0)
                   {
                    childrens.Add((int)((UInt32)mo["ProcessID"]));

                    if (DateTime.Compare(first_seen, createAsyncDate) > 0) first_seen = createAsyncDate;
                    if (DateTime.Compare(last_update, createAsyncDate) < 0) last_update = createAsyncDate;
                   }
                  }
                 }
                 mos.Dispose();

                 Console.WriteLine("Add process into database: pid - {0}, name - {1}", childProcessId, ProcessName);
                 {
                  ManagementObjectSearcher searcher_subprocesses = new ManagementObjectSearcher("SELECT * FROM Win32_Process WHERE ParentProcessId=" + childProcessId);

                  ManagementObjectCollection collection_subprocesses = searcher_subprocesses.Get();

                  if (collection_subprocesses.Count > 0)
                  {
                   string tmp_output = "Процесс с идентификатором " + childProcessId + " на старте имеет следующие подпроцессы: ";
                   foreach (var item_subprocesses in collection_subprocesses)
                   {
                    string ProcessName_subprocesses = Path.GetFileNameWithoutExtension((string)item_subprocesses["Name"]);

                    tmp_output += ("'" + ProcessName_subprocesses + "' ");
                   }
                   Console.WriteLine(tmp_output);
                  }
                 }

                 catched_miners.Add((int)childProcessId);
                 catched_miners_start_datetime.Add(createDate);

                 childrens_of_catched_miners.Add(childrens);

                 catched_childrens_of_miners_first_seen.Add(first_seen);

                 if (childrens.Count > 0)
                  catched_childrens_of_miners_last_update.Add(last_update);
                 else
                  catched_childrens_of_miners_last_update.Add(DateTime.Now);
                }

                minersProcessesCounter++;
               }
              }
              catch { }
             }
            }
           }

           foreach (int shutdowned_pid_index in indexesOfUnusedProcesses)
           {
            if (Program.ru_lang)
             Console.WriteLine("Процесс с идентификатором '{0}' был завершен, удаляем дочерние процессы, если они есть", catched_miners[shutdowned_pid_index]);
            else
             Console.WriteLine("Process with pid '{0}' has been shutdowned, clear child processes, if they are exist", catched_miners[shutdowned_pid_index]);

            ManagementObjectSearcher mos = new ManagementObjectSearcher(String.Format(null, "Select * From Win32_Process Where ParentProcessID={0}", catched_miners[shutdowned_pid_index]));
            ManagementObjectCollection mos_collection = mos.Get();

            foreach (ManagementObject mo in mos_collection)
            {
             if (ConvertFromWmiDate((string)mo["CreationDate"], out DateTime moDate))
             {
              if (DateTime.Compare(moDate, catched_childrens_of_miners_first_seen[shutdowned_pid_index]) >= 0 && DateTime.Compare(moDate, catched_childrens_of_miners_last_update[shutdowned_pid_index]) <= 0)
              {
               try
               {
                int childProcessId = (int)((UInt32)mo["ProcessId"]);
                string ProcessName = Path.GetFileNameWithoutExtension((string)mo["Name"]);

                Console.WriteLine("WMIC autokill '{0}', pid: {1}", ProcessName, childProcessId);

                Process killProcess = new Process();
                killProcess.StartInfo.UseShellExecute = false;
                killProcess.StartInfo.FileName = "wmic";
                killProcess.StartInfo.CreateNoWindow = true;
                killProcess.StartInfo.ErrorDialog = false;

                killProcess.StartInfo.Arguments = "process where (processid=\"" + childProcessId + "\" and parentprocessid=\"" + catched_miners[shutdowned_pid_index] + "\") call terminate";

                killProcess.Start();
                killProcess.WaitForExit();
                killProcess.Close();
                killProcess.Dispose();
                killProcess = null;

                GC.Collect();
               }
               catch { }
              }
             }
            }

            mos.Dispose();
           }

           bool have_killed_miners = (indexesOfUnusedProcesses.Count > 0);
           if (have_killed_miners)
           {
            List<DateTime> catched_childrens_of_miners_first_seen_new = new List<DateTime>();
            List<DateTime> catched_childrens_of_miners_last_update_new = new List<DateTime>();

            List<int> catched_miners_new = new List<int>();
            List<DateTime> catched_miners_start_datetime_new = new List<DateTime>();

            List<List<int>> childrens_of_catched_miners_new = new List<List<int>>();

            for (int psta = 0; psta < catched_miners.Count; psta++)
            {
             if (!indexesOfUnusedProcesses.Contains(psta))
             {
              catched_childrens_of_miners_first_seen_new.Add(catched_childrens_of_miners_first_seen[psta]);
              catched_childrens_of_miners_last_update_new.Add(catched_childrens_of_miners_last_update[psta]);

              catched_miners_new.Add(catched_miners[psta]);
              catched_miners_start_datetime_new.Add(catched_miners_start_datetime[psta]);

              childrens_of_catched_miners_new.Add(childrens_of_catched_miners[psta]);
             }
            }

            catched_childrens_of_miners_first_seen = catched_childrens_of_miners_first_seen_new;
            catched_childrens_of_miners_last_update = catched_childrens_of_miners_last_update_new;

            catched_miners = catched_miners_new;
            catched_miners_start_datetime = catched_miners_start_datetime_new;

            childrens_of_catched_miners = childrens_of_catched_miners_new;
           }

           if (have_killed_miners && catched_miners.Count == 0 && exitShutdownCounter == 0) exitShutdown = true;

           if (Program.main_cycle_enabled && exitShutdown == true)
           {
            if (exitShutdownCounter == 0)
            {
             exit_timer = DateTime.Now;

             if (Program.ru_lang)
              Console.WriteLine("Даем временной отрезок до " + (options.wait_timeout / 4.0).ToString("0.000").Replace(',', '.') + " секунд на старт новых майнеров (т.е. четверть от базового периода ожидания, меньше в случае завершения сценария добычи)");
             else
              Console.WriteLine("We give a time period up to " + (options.wait_timeout / 4.0).ToString("0.000").Replace(',', '.') + " seconds to start new miners (that is a quarter of the base waiting period, less if mining scenario ends)");

             if (mainThread_enabled)
             {
              lock (timeOfLatestAccepted_SyncObject)
              {
               timeOfLatestAccepted = (object)DateTime.Now;

               acceptedTimeOut = (object)(options.share_wait_timeout + 360);

               Console.WriteLine("Переинициализировали слежение за шарами, первую шару ждем {0}.000 секунд; следующие шары на 360 секунд меньше", ((int)acceptedTimeOut).ToString());
              }
             }

             exitShutdownCounter = 1;

             Thread.Sleep(sleep_default_timeout);

             searcher.Dispose();

             continue;
            }
            else
            {
             exitShutdownCounter++;

             Thread.Sleep(sleep_default_timeout);

             if ((DateTime.Now - exit_timer).TotalMilliseconds < (options.wait_timeout / 4.0 * 1000))
             {
              try
              {
               if (curr_process != null)
               {
                if (curr_process.HasExited)
                {
                 searcher.Dispose();

                 break;
                }
               }
              }
              catch { }

              searcher.Dispose();

              continue;
             }
             else
             {
              exitShutdown = false;
             }
            }
           }

           if (catched_miners.Count == 0 && exitShutdown == false)
           {
            if (Program.ru_lang)
             Console.WriteLine("Нет процессов, успевших стартовать, завершаем сценарий");
            else
             Console.WriteLine("There are no started processes, we complete the scenario");

            if (options.direct_order == 1)
            {
             try
             {
              Process killProcessA = new Process();
              killProcessA.StartInfo.UseShellExecute = false;
              killProcessA.StartInfo.FileName = "wmic";
              killProcessA.StartInfo.CreateNoWindow = true;
              killProcessA.StartInfo.ErrorDialog = false;

              killProcessA.StartInfo.Arguments = "process where (processid=\"" + processInfo.dwProcessId + "\" and parentprocessid=\"" + Process.GetCurrentProcess().Id + "\") call terminate";

              killProcessA.Start();
              killProcessA.WaitForExit();
              killProcessA.Close();
              killProcessA.Dispose();
              killProcessA = null;

              GC.Collect();
             }
             catch { }

             KillAllProcessesSpawnedBy(timeOfStart, (uint)processInfo.dwProcessId, DateTime.Now);
            }
            else
            {
             KillAllProcessesSpawnedBy(timeOfStart, (uint)processInfo.dwProcessId, DateTime.Now);

             try
             {
              Process killProcessA = new Process();
              killProcessA.StartInfo.UseShellExecute = false;
              killProcessA.StartInfo.FileName = "wmic";
              killProcessA.StartInfo.CreateNoWindow = true;
              killProcessA.StartInfo.ErrorDialog = false;

              killProcessA.StartInfo.Arguments = "process where (processid=\"" + processInfo.dwProcessId + "\" and parentprocessid=\"" + Process.GetCurrentProcess().Id + "\") call terminate";

              killProcessA.Start();
              killProcessA.WaitForExit();
              killProcessA.Close();
              killProcessA.Dispose();
              killProcessA = null;

              GC.Collect();
             }
             catch { }
            }

            searcher.Dispose();

            break;
           }
           else if (catched_miners.Count > 0)
           {
            exitShutdownCounter = 0;

            exitShutdown = false;
           }
          }
          else
          {
           searcher.Dispose();

           break;
          }

          searcher.Dispose();

          Thread.Sleep(sleep_default_timeout);
         }
        }
       }

       if (catched_miners.Count > 0)
       {
        Thread.Sleep(1500);

        if (Program.ru_lang)
         Console.WriteLine("Старт ожидания завершения добычи");
        else
         Console.WriteLine("Start waiting end of mining process");

        for (int ii = 0; ii < catched_miners.Count; ii++)
        {
         int catched_pid = catched_miners[ii];

         try
         {
          while (Process.GetProcesses().Any(x => x.Id == catched_pid))
          {
           Process childProcess = Process.GetProcessById(catched_pid);

           if (childProcess.StartTime == catched_miners_start_datetime[ii]) Thread.Sleep(500);
           else break;
          }
         }
         catch { }

         KillAllProcessesSpawnedBy(catched_miners_start_datetime[ii], (uint)catched_pid, DateTime.Now);
        }

        if (Program.ru_lang)
         Console.WriteLine("Конец ожидания завершения добычи");
        else
         Console.WriteLine("Mining process is completed");
       }
      });
     }
     else
     {
      if (mainThread_enabled)
      {
       lock (timeOfLatestAccepted_SyncObject)
       {
        timeOfLatestAccepted = (object)DateTime.Now;

        acceptedTimeOut = (object)(options.share_wait_timeout + 360);

        Console.WriteLine("Инициализировали слежение за шарами, первую шару ждем {0}.000 секунд; следующие шары на 360 секунд меньше", ((int)acceptedTimeOut).ToString());
       }
      }
     }

     if (options.without_external_windows == 1)
     {
      outThread.IsBackground = true;
      outThread.Start();

      errThread.IsBackground = true;
      errThread.Start();
     }

     if (isBatFile)
     {
      asyncThread.IsBackground = true;
      asyncThread.Start();

      while (curr_process != null)
      {
       curr_process.WaitForExit(1000);

       if (curr_process.HasExited) break;
       else if (options.without_external_windows == 1)
       {
        if (!outThread.IsAlive && !errThread.IsAlive) break;
       }
      }

      if (Program.ru_lang)
       Console.WriteLine("Основные процессы сценария добычи запущены");
      else
       Console.WriteLine("Key mining scenario processes are worked out");
     }
     else
     {
      while (curr_process != null)
      {
       curr_process.WaitForExit(1000);

       if (curr_process.HasExited) break;
       else if (options.without_external_windows == 1)
       {
        if (!outThread.IsAlive && !errThread.IsAlive) break;
       }
      }
     }

     switch (pvar)
     {
      case ProcessVariant.Master:
       {
        masterMinerProcessDeathTime = DateTime.Now;

        break;
       }
      case ProcessVariant.Slave0:
       {
        slaveMinerProcess0DeathTime = DateTime.Now;

        break;
       }
      case ProcessVariant.Slave1:
       {
        slaveMinerProcess1DeathTime = DateTime.Now;

        break;
       }
     }

     if (options.without_external_windows == 1)
     {
      outThread.Join();

      errThread.Join();
     }

     if (Program.ru_lang)
      Console.WriteLine("Треды отслеживания сообщений завершили работу");
     else
      Console.WriteLine("The tracking threads for messages are finished own work");

     if (isBatFile) asyncThread.Join();

     Thread.Sleep(sleep_default_timeout);

     if (options.direct_order == 1)
     {
      try
      {
       Process killProcess = new Process();
       killProcess.StartInfo.UseShellExecute = false;
       killProcess.StartInfo.FileName = "wmic";
       killProcess.StartInfo.CreateNoWindow = true;
       killProcess.StartInfo.ErrorDialog = false;

       killProcess.StartInfo.Arguments = "process where (processid=\"" + processInfo.dwProcessId + "\" and parentprocessid=\"" + Process.GetCurrentProcess().Id + "\") call terminate";

       killProcess.Start();
       killProcess.WaitForExit();
       killProcess.Close();
       killProcess.Dispose();
       killProcess = null;

       GC.Collect();
      }
      catch { }

      Thread.Sleep(250);

      switch (pvar)
      {
       case ProcessVariant.Master:
        {
         KillAllProcessesSpawnedBy(masterMinerProcessStartTime, (uint)processInfo.dwProcessId, masterMinerProcessDeathTime);

         break;
        }
       case ProcessVariant.Slave0:
        {
         KillAllProcessesSpawnedBy(slaveMinerProcess0StartTime, (uint)processInfo.dwProcessId, slaveMinerProcess0DeathTime);

         break;
        }
       case ProcessVariant.Slave1:
        {
         KillAllProcessesSpawnedBy(slaveMinerProcess1StartTime, (uint)processInfo.dwProcessId, slaveMinerProcess1DeathTime);

         break;
        }
      }
     }
     else
     {
      switch (pvar)
      {
       case ProcessVariant.Master:
        {
         KillAllProcessesSpawnedBy(masterMinerProcessStartTime, (uint)processInfo.dwProcessId, masterMinerProcessDeathTime);

         break;
        }
       case ProcessVariant.Slave0:
        {
         KillAllProcessesSpawnedBy(slaveMinerProcess0StartTime, (uint)processInfo.dwProcessId, slaveMinerProcess0DeathTime);

         break;
        }
       case ProcessVariant.Slave1:
        {
         KillAllProcessesSpawnedBy(slaveMinerProcess1StartTime, (uint)processInfo.dwProcessId, slaveMinerProcess1DeathTime);

         break;
        }
      }

      Thread.Sleep(250);

      try
      {
       Process killProcess = new Process();
       killProcess.StartInfo.UseShellExecute = false;
       killProcess.StartInfo.FileName = "wmic";
       killProcess.StartInfo.CreateNoWindow = true;
       killProcess.StartInfo.ErrorDialog = false;

       killProcess.StartInfo.Arguments = "process where (processid=\"" + processInfo.dwProcessId + "\" and parentprocessid=\"" + Process.GetCurrentProcess().Id + "\") call terminate";

       killProcess.Start();
       killProcess.WaitForExit();
       killProcess.Close();
       killProcess.Dispose();
       killProcess = null;

       GC.Collect();
      }
      catch { }

      Thread.Sleep(3000);

      try
      {
       if (ProcessHelpers.IsRunning(Path.GetFileNameWithoutExtension("OhGodAnETHlargementPill-r2.exe")))
       {
        Process killProcess = new Process();
        killProcess.StartInfo.UseShellExecute = false;
        killProcess.StartInfo.FileName = "wmic";
        killProcess.StartInfo.CreateNoWindow = true;
        killProcess.StartInfo.ErrorDialog = false;

        killProcess.StartInfo.Arguments = "process where name=\"OhGodAnETHlargementPill-r2.exe\" call terminate";

        killProcess.Start();
        killProcess.WaitForExit();
        killProcess.Close();
        killProcess.Dispose();
        killProcess = null;
        GC.Collect();
       }
      }
      catch { }

#if kill_msi_after_exec
      try
      {
       if (ProcessHelpers.IsRunning(Path.GetFileNameWithoutExtension("MSIAfterburner.exe")))
       {
        Process killProcess = new Process();
        killProcess.StartInfo.UseShellExecute = false;
        killProcess.StartInfo.FileName = "wmic";
        killProcess.StartInfo.CreateNoWindow = true;
        killProcess.StartInfo.ErrorDialog = false;

        killProcess.StartInfo.Arguments = "process where name=\"MSIAfterburner.exe\" call terminate";

        killProcess.Start();
        killProcess.WaitForExit();
        killProcess.Close();
        killProcess.Dispose();
        killProcess = null;
        GC.Collect();
       }
      }
      catch { }
#endif
     }
    }

    if (options.without_external_windows == 1)
    {
     readerStdOut.Dispose();
     shStdOutRead.Dispose();
     readerStdErr.Dispose();
     shStdErrRead.Dispose();

     if (redirect_input)
     {
      writerStdIn.Dispose();
      shStdInWrite.Dispose();
     }
    }

    CloseHandle(processInfo.hProcess);
    CloseHandle(processInfo.hThread);

    switch (pvar)
    {
     case ProcessVariant.Master:
      {
       masterMinerProcess = null;
       masterMinerProcessId = -1;

       break;
      }
     case ProcessVariant.Slave0:
      {
       slaveMinerProcess0 = null;
       slaveMinerProcess0Id = -1;

       break;
      }
     case ProcessVariant.Slave1:
      {
       slaveMinerProcess1 = null;
       slaveMinerProcess1Id = -1;

       break;
      }
    }

    GC.Collect();
   }
   catch { }
  }

  static volatile int sleep_timeout = 0;

  readonly static AutoResetEvent evt = new AutoResetEvent(false);
  readonly static AutoResetEvent evt_main = new AutoResetEvent(false);

  readonly static WshShell shell = new WshShell();

  static volatile object listener_status = 0;

  [SecurityCritical]
  readonly static Thread listener = new Thread(() =>
  {
   while (true)
   {
    evt.WaitOne();

    for (int i = 0; i < 10; i++)
    {
     Thread.Sleep(sleep_timeout / 10);

     lock (listener_status)
     {
      bool for_need_exit = false;

      if ((int)listener_status == 1)
      {
       bool print_message = false;

       if (slaveMinerProcess0 == null && slaveMinerProcess1 == null && masterMinerProcess == null)
       {
        print_message = true;

        Console.WriteLine("Executed snippet 0");
       }
       else
       {
        if (slaveMinerProcess0 != null) if (slaveMinerProcess0.HasExited) print_message = true;
        if (slaveMinerProcess1 != null) if (slaveMinerProcess1.HasExited) print_message = true;
        if (masterMinerProcess != null) if (masterMinerProcess.HasExited) print_message = true;

        if (print_message) Console.WriteLine("Executed snippet 1");
       }

       if (print_message)
       {
        listener_status = 0;

        if (Program.ru_lang)
         Console.WriteLine("Быстрый выход, возможно Вы должны запускать PoolWatcher с правами администратора");
        else
         Console.WriteLine("Fast exit, maybe You must set admin rights to PoolWatcher");

        for_need_exit = true;
       }
      }
      else
      {
       for_need_exit = true;
      }

      if (for_need_exit) break;
     }
    }

    evt_main.Set();
   }
  });

  static volatile Task start_task = new Task(() => { });

  static volatile bool waitAfterBan = false;     // Основная монета, проверка статуса пула

  static volatile object timeOfLatestBan_SyncObject = new object();
  static volatile object timeOfLatestBan = null; // Объект для хранения времени последнего бана

  static volatile object acceptedTimeOut = 0;
  static volatile object timeOfLatestAccepted_SyncObject = new object();
  static volatile object timeOfLatestAccepted = null; // Объект для хранения времени последней принятой шары

  [SecurityCritical]
  static void BanWaitBody(Task t)
  {
   lock (_lockObj)
   {
    Console.WriteLine($"Выполняется бан-задача с идентификатором '{Task.CurrentId}'");

    waitAfterBan = true;

    Console.ForegroundColor = ConsoleColor.Magenta;
    Console.WriteLine("Run pool ban: {0} minutes", options.ban_timeout);
    Console.ForegroundColor = ConsoleColor.White;

    Thread.Sleep(options.ban_timeout * 60 * 1000); // ждем завершения бана

    Console.ForegroundColor = ConsoleColor.Magenta;
    Console.WriteLine("Pool ban completed");
    Console.ForegroundColor = ConsoleColor.White;

    start_task = t;

    waitAfterBan = false;
   }
  }

  [SecurityCritical]
  static void runBan()
  {
   start_task.ContinueWith(BanWaitBody);
  }

  static volatile bool waitAfterKill = true;

  static volatile bool killIsActive = false;

  static volatile bool dummy_status_ok = false;

  static volatile bool main_cycle_enabled = true;

  static volatile bool mainThread_enabled = false;
  static volatile bool slave0thread = false;
  static volatile bool slave1thread = false;

  static volatile Process slaveMinerProcess0, slaveMinerProcess1, masterMinerProcess;
  static volatile int slaveMinerProcess0Id, slaveMinerProcess1Id, masterMinerProcessId;

  static DateTime slaveMinerProcess0StartTime = DateTime.Now, slaveMinerProcess1StartTime = DateTime.Now, masterMinerProcessStartTime = DateTime.Now;
  static DateTime slaveMinerProcess0DeathTime = DateTime.Now, slaveMinerProcess1DeathTime = DateTime.Now, masterMinerProcessDeathTime = DateTime.Now;

  static volatile Thread mainThreadStart, slave0ThreadStart, slave1ThreadStart;

  static readonly object lobj = new object();

  static volatile bool ru_lang;

  [SecurityCritical]
  static void KillAllProcessesSpawnedBy(DateTime createObjectTime, UInt32 parentProcessId, DateTime deathTime)
  {
   ManagementObjectSearcher searcher = new ManagementObjectSearcher(
      "SELECT * " +
      "FROM Win32_Process " +
      "WHERE ParentProcessId=" + parentProcessId);

   ManagementObjectCollection collection = searcher.Get();
   if (collection.Count > 0)
   {
    foreach (var item in collection)
    {
     if (ConvertFromWmiDate((string)item["CreationDate"], out DateTime createDate))
     {
      if (DateTime.Compare(createObjectTime, createDate) <= 0 && DateTime.Compare(deathTime, createDate) > 0)
      {
       UInt32 childProcessId = (UInt32)item["ProcessId"];

       try
       {
        string ProcessName = Path.GetFileNameWithoutExtension((string)item["Name"]);

        if (ProcessName == "conhost")
        {
         Console.WriteLine("Kill conhost process start, pid: {0}", childProcessId);

         if (options.direct_order == 1)
         {
          Process killProcess = new Process();
          killProcess.StartInfo.UseShellExecute = false;
          killProcess.StartInfo.FileName = "wmic";
          killProcess.StartInfo.CreateNoWindow = true;
          killProcess.StartInfo.ErrorDialog = false;

          killProcess.StartInfo.Arguments = "process where processid=\"" + childProcessId + "\" call terminate";

          killProcess.Start();
          killProcess.WaitForExit();
          killProcess.Close();
          killProcess.Dispose();
          killProcess = null;

          KillAllProcessesSpawnedBy(createDate, childProcessId, DateTime.Now);
         }
         else
         {
          KillAllProcessesSpawnedBy(createDate, childProcessId, DateTime.Now);

          Process killProcess = new Process();
          killProcess.StartInfo.UseShellExecute = false;
          killProcess.StartInfo.FileName = "wmic";
          killProcess.StartInfo.CreateNoWindow = true;
          killProcess.StartInfo.ErrorDialog = false;

          killProcess.StartInfo.Arguments = "process where processid=\"" + childProcessId + "\" call terminate";

          killProcess.Start();
          killProcess.WaitForExit();
          killProcess.Close();
          killProcess.Dispose();
          killProcess = null;
         }

         try
         {
          while (Process.GetProcesses().Any(x => x.Id == childProcessId))
          {
           Process childProcess = Process.GetProcessById((int)childProcessId);

           if (childProcess.StartTime == createDate) Thread.Sleep(500);
           else break;
          }
         }
         catch { }

         Console.WriteLine("Kill conhost process end");

         GC.Collect();
        }
       }
       catch { }
      }
     }
    }

    Thread.Sleep(100);

    foreach (var item in collection)
    {
     if (ConvertFromWmiDate((string)item["CreationDate"], out DateTime createDate))
     {
      if (DateTime.Compare(createObjectTime, createDate) <= 0 && DateTime.Compare(deathTime, createDate) > 0)
      {
       UInt32 childProcessId = (UInt32)item["ProcessId"];

       try
       {
        string ProcessName = Path.GetFileNameWithoutExtension((string)item["Name"]);

        if (ProcessName == "OhGodAnETHlargementPill-r2")
        {
         Console.WriteLine("Kill pill process start, pid: {0}", childProcessId);

         if (options.direct_order == 1)
         {
          Process killProcess = new Process();
          killProcess.StartInfo.UseShellExecute = false;
          killProcess.StartInfo.FileName = "wmic";
          killProcess.StartInfo.CreateNoWindow = true;
          killProcess.StartInfo.ErrorDialog = false;

          killProcess.StartInfo.Arguments = "process where processid=\"" + childProcessId + "\" call terminate";

          killProcess.Start();
          killProcess.WaitForExit();
          killProcess.Close();
          killProcess.Dispose();
          killProcess = null;

          KillAllProcessesSpawnedBy(createDate, childProcessId, DateTime.Now);
         }
         else
         {
          KillAllProcessesSpawnedBy(createDate, childProcessId, DateTime.Now);

          Process killProcess = new Process();
          killProcess.StartInfo.UseShellExecute = false;
          killProcess.StartInfo.FileName = "wmic";
          killProcess.StartInfo.CreateNoWindow = true;
          killProcess.StartInfo.ErrorDialog = false;

          killProcess.StartInfo.Arguments = "process where processid=\"" + childProcessId + "\" call terminate";

          killProcess.Start();
          killProcess.WaitForExit();
          killProcess.Close();
          killProcess.Dispose();
          killProcess = null;
         }

         try
         {
          while (Process.GetProcesses().Any(x => x.Id == childProcessId))
          {
           Process childProcess = Process.GetProcessById((int)childProcessId);

           if (childProcess.StartTime == createDate) Thread.Sleep(500);
           else break;
          }
         }
         catch { }

         Thread.Sleep(10000);

         Console.WriteLine("Kill pill process end");

         GC.Collect();
        }
       }
       catch { }
      }
     }
    }

    Thread.Sleep(100);

    foreach (var item in collection)
    {
     if (ConvertFromWmiDate((string)item["CreationDate"], out DateTime createDate))
     {
      if (DateTime.Compare(createObjectTime, createDate) <= 0 && DateTime.Compare(deathTime, createDate) > 0)
      {
       UInt32 childProcessId = (UInt32)item["ProcessId"];

       try
       {
        string ProcessName = Path.GetFileNameWithoutExtension((string)item["Name"]);

        if (ProcessName != "conhost" && ProcessName != "OhGodAnETHlargementPill-r2" && ProcessName != "MSIAfterburner")
        {
         Console.WriteLine("Kill other process ({0}) start, pid: {1}", ProcessName, childProcessId);

         if (options.direct_order == 1)
         {
          Process killProcess = new Process();
          killProcess.StartInfo.UseShellExecute = false;
          killProcess.StartInfo.FileName = "wmic";
          killProcess.StartInfo.CreateNoWindow = true;
          killProcess.StartInfo.ErrorDialog = false;

          killProcess.StartInfo.Arguments = "process where processid=\"" + childProcessId + "\" call terminate";

          killProcess.Start();
          killProcess.WaitForExit();
          killProcess.Close();
          killProcess.Dispose();
          killProcess = null;

          KillAllProcessesSpawnedBy(createDate, childProcessId, DateTime.Now);
         }
         else
         {
          KillAllProcessesSpawnedBy(createDate, childProcessId, DateTime.Now);

          Process killProcess = new Process();
          killProcess.StartInfo.UseShellExecute = false;
          killProcess.StartInfo.FileName = "wmic";
          killProcess.StartInfo.CreateNoWindow = true;
          killProcess.StartInfo.ErrorDialog = false;

          killProcess.StartInfo.Arguments = "process where processid=\"" + childProcessId + "\" call terminate";

          killProcess.Start();
          killProcess.WaitForExit();
          killProcess.Close();
          killProcess.Dispose();
          killProcess = null;
         }

         try
         {
          while (Process.GetProcesses().Any(x => x.Id == childProcessId))
          {
           Process childProcess = Process.GetProcessById((int)childProcessId);

           if (childProcess.StartTime == createDate) Thread.Sleep(500);
           else break;
          }
         }
         catch { }

         Console.WriteLine("Kill other process ({0}) end", ProcessName);

         GC.Collect();
        }
       }
       catch { }
      }
     }
    }

#if kill_msi_after_exec
    Thread.Sleep(100);

    foreach (var item in collection)
    {
     if (ConvertFromWmiDate((string)item["CreationDate"], out DateTime createDate))
     {
      if (DateTime.Compare(createObjectTime, createDate) <= 0 && DateTime.Compare(deathTime, createDate) > 0)
      {
       UInt32 childProcessId = (UInt32)item["ProcessId"];

       try
       {
        string ProcessName = Path.GetFileNameWithoutExtension((string)item["Name"]);

        if (ProcessName == "MSIAfterburner")
        {
         Console.WriteLine("Kill burner process start, pid: {0}", childProcessId);

         if (options.direct_order == 1)
         {
          Process killProcess = new Process();
          killProcess.StartInfo.UseShellExecute = false;
          killProcess.StartInfo.FileName = "wmic";
          killProcess.StartInfo.CreateNoWindow = true;
          killProcess.StartInfo.ErrorDialog = false;

          killProcess.StartInfo.Arguments = "process where processid=\"" + childProcessId + "\" call terminate";

          killProcess.Start();
          killProcess.WaitForExit();
          killProcess.Close();
          killProcess.Dispose();
          killProcess = null;

          KillAllProcessesSpawnedBy(createDate, childProcessId, DateTime.Now);
         }
         else
         {
          KillAllProcessesSpawnedBy(createDate, childProcessId, DateTime.Now);

          Process killProcess = new Process();
          killProcess.StartInfo.UseShellExecute = false;
          killProcess.StartInfo.FileName = "wmic";
          killProcess.StartInfo.CreateNoWindow = true;
          killProcess.StartInfo.ErrorDialog = false;

          killProcess.StartInfo.Arguments = "process where processid=\"" + childProcessId + "\" call terminate";

          killProcess.Start();
          killProcess.WaitForExit();
          killProcess.Close();
          killProcess.Dispose();
          killProcess = null;
         }

         try
         {
          while (Process.GetProcesses().Any(x => x.Id == childProcessId))
          {
           Process childProcess = Process.GetProcessById((int)childProcessId);

           if (childProcess.StartTime == createDate) Thread.Sleep(500);
           else break;
          }
         }
         catch { }

         Console.WriteLine("Kill burner process end");

         GC.Collect();
        }
       }
       catch { }
      }
     }
    }
#endif
   }

   searcher.Dispose();
   GC.Collect();
  }

  [SecurityCritical]
  static void TaskKillByArg(string arg, int minerid)
  {
   try
   {
    lock (lobj)
    {
     if (Program.ru_lang)
      Console.WriteLine("Антивотчдог-система стартовала");
     else
      Console.WriteLine("Aanti-watchdog system started");
    }

    Thread.Sleep(3500);

    string filename = (new FileInfo(arg)).Name;
    string exe_filename;
    if (filename.EndsWith(".bat", StringComparison.CurrentCulture))
    {
     exe_filename = Path.ChangeExtension(filename, ".exe");
    }
    else if (filename.EndsWith(".lnk", StringComparison.CurrentCulture))
    {
     IWshShortcut link = (IWshShortcut)shell.CreateShortcut(filename);
     exe_filename = link.TargetPath;
     if (exe_filename.EndsWith(".bat", StringComparison.CurrentCulture)) exe_filename = Path.ChangeExtension(exe_filename, ".exe");
    }
    else
    {
     exe_filename = filename;
    }

    lock (lobj)
    {
     if (Program.ru_lang)
      Console.WriteLine("Ожидайте завершения работы майнера '" + minerid + "' (" + exe_filename + "), осталось не более 15 секунд (антивотчдог-система)!");
     else
      Console.WriteLine("Expect completion of miner '" + minerid + "' (" + exe_filename + ") work, no more than 15 seconds left (anti-watchdog system)!");
    }

    for (int i = 0; i < 90; i++)
    {
     if (ProcessHelpers.IsRunning(Path.GetFileNameWithoutExtension(exe_filename)))
     {
      Process killProcess = new Process();
      killProcess.StartInfo.UseShellExecute = false;
      killProcess.StartInfo.FileName = "wmic";
      killProcess.StartInfo.CreateNoWindow = true;
      killProcess.StartInfo.ErrorDialog = false;
      killProcess.StartInfo.Arguments = "process where name=\"" + exe_filename + "\" call terminate";
      killProcess.Start();
      killProcess.WaitForExit();
      killProcess.Close();
      killProcess.Dispose();
      killProcess = null;
      GC.Collect();

      break;
     }
     else
     {
      Thread.Sleep(200);
     }
    }

    lock (lobj)
    {
     if (Program.ru_lang)
      Console.WriteLine("Антивотчдог-система завершила работу");
     else
      Console.WriteLine("Aanti-watchdog system stopped");
    }
   }
   catch { }
  }

  [SecurityCritical]
  static void TaskKill()
  {
   try
   {
    string[] args_backup_names = new string[exe_lines.Length];
    for (int i = 0; i < exe_lines.Length; i++)
    {
     try
     {
      args_backup_names[i] = (new FileInfo(exe_lines[i])).Name;
     }
     catch { }

     if (!String.IsNullOrEmpty(args_backup_names[i]))
     {
      if (args_backup_names.IndexOf<string>(args_backup_names[i]) == i)
      {
       string exe_filename;

       Process killProcess = new Process();
       killProcess.StartInfo.UseShellExecute = false;
       killProcess.StartInfo.FileName = "wmic";
       killProcess.StartInfo.CreateNoWindow = true;
       killProcess.StartInfo.ErrorDialog = false;

       if (args_backup_names[i].EndsWith(".bat", StringComparison.CurrentCulture))
       {
        exe_filename = Path.ChangeExtension(args_backup_names[i], ".exe");
       }
       else if (args_backup_names[i].EndsWith(".lnk", StringComparison.CurrentCulture))
       {
        IWshShortcut link = (IWshShortcut)shell.CreateShortcut(args_backup_names[i]);
        exe_filename = link.TargetPath;
        if (exe_filename.EndsWith(".bat", StringComparison.CurrentCulture)) exe_filename = Path.ChangeExtension(exe_filename, ".exe");
       }
       else
       {
        exe_filename = args_backup_names[i];
       }

       killProcess.StartInfo.Arguments = "process where name=\"" + exe_filename + "\" call terminate";

       killProcess.Start();
       killProcess.WaitForExit();
       killProcess.Close();
       killProcess.Dispose();
       killProcess = null;
       GC.Collect();

       Thread.Sleep(1250);

       int errCounter = 0;
       while (ProcessHelpers.IsRunning(Path.GetFileNameWithoutExtension(exe_filename)))
       {
        if (Program.ru_lang)
         Console.WriteLine("Не удается добить '" + exe_filename + "'!");
        else
         Console.WriteLine("Can't kill '" + exe_filename + "'!");

        Thread.Sleep(2000);

        errCounter++;

        if (errCounter == 3)
        {
         if (Program.ru_lang)
          Console.WriteLine("Подпрограмма, отслеживающая завершения исполняемого файла, не смогла дождаться его завершения");
         else
          Console.WriteLine("Subprogram that tracks the completion of the executable file: could not wait for its completion");

         break;
        }
       }
      }
     }
    }
   }
   catch { }
  }

  [SecurityCritical]
  static void killPill()
  {
   try
   {
    if (ProcessHelpers.IsRunning(Path.GetFileNameWithoutExtension("OhGodAnETHlargementPill-r2.exe")))
    {
     Process killProcess = new Process();
     killProcess.StartInfo.UseShellExecute = false;
     killProcess.StartInfo.FileName = "wmic";
     killProcess.StartInfo.CreateNoWindow = true;
     killProcess.StartInfo.ErrorDialog = false;

     killProcess.StartInfo.Arguments = "process where name=\"OhGodAnETHlargementPill-r2.exe\" call terminate";

     killProcess.Start();
     killProcess.WaitForExit();
     killProcess.Close();
     killProcess.Dispose();
     killProcess = null;
     GC.Collect();

     Thread.Sleep(10000);
    }
   }
   catch { }
  }

  [SecurityCritical]
  static void KillMiner()
  {
   killIsActive = true;

   mainThread_enabled = false;
   slave0thread = false;
   slave1thread = false;

   try
   {
    if (masterMinerProcessId != -1)
    {
     if (options.direct_order == 1)
     {
      Process killProcess = new Process();
      killProcess.StartInfo.UseShellExecute = false;
      killProcess.StartInfo.FileName = "wmic";
      killProcess.StartInfo.CreateNoWindow = true;
      killProcess.StartInfo.ErrorDialog = false;

      killProcess.StartInfo.Arguments = "process where (processid=\"" + (uint)masterMinerProcessId + "\" and parentprocessid=\"" + Process.GetCurrentProcess().Id + "\") call terminate";

      killProcess.Start();
      killProcess.WaitForExit();
      killProcess.Close();
      killProcess.Dispose();
      killProcess = null;

      KillAllProcessesSpawnedBy(masterMinerProcessStartTime, (uint)masterMinerProcessId, masterMinerProcessDeathTime);
     }
     else
     {
      KillAllProcessesSpawnedBy(masterMinerProcessStartTime, (uint)masterMinerProcessId, masterMinerProcessDeathTime);

      Process killProcess = new Process();
      killProcess.StartInfo.UseShellExecute = false;
      killProcess.StartInfo.FileName = "wmic";
      killProcess.StartInfo.CreateNoWindow = true;
      killProcess.StartInfo.ErrorDialog = false;

      killProcess.StartInfo.Arguments = "process where (processid=\"" + (uint)masterMinerProcessId + "\" and parentprocessid=\"" + Process.GetCurrentProcess().Id + "\") call terminate";

      killProcess.Start();
      killProcess.WaitForExit();
      killProcess.Close();
      killProcess.Dispose();
      killProcess = null;
     }

     if (masterMinerProcess != null) masterMinerProcess.WaitForExit();

     GC.Collect();
    }
   }
   catch { }

   try
   {
    if (slaveMinerProcess0Id != -1)
    {
     if (options.direct_order == 1)
     {
      Process killProcess = new Process();
      killProcess.StartInfo.UseShellExecute = false;
      killProcess.StartInfo.FileName = "wmic";
      killProcess.StartInfo.CreateNoWindow = true;
      killProcess.StartInfo.ErrorDialog = false;

      killProcess.StartInfo.Arguments = "process where (processid=\"" + (uint)slaveMinerProcess0Id + "\" and parentprocessid=\"" + Process.GetCurrentProcess().Id + "\") call terminate";

      killProcess.Start();
      killProcess.WaitForExit();
      killProcess.Close();
      killProcess.Dispose();
      killProcess = null;

      KillAllProcessesSpawnedBy(slaveMinerProcess0StartTime, (uint)slaveMinerProcess0Id, slaveMinerProcess0DeathTime);
     }
     else
     {
      KillAllProcessesSpawnedBy(slaveMinerProcess0StartTime, (uint)slaveMinerProcess0Id, slaveMinerProcess0DeathTime);

      Process killProcess = new Process();
      killProcess.StartInfo.UseShellExecute = false;
      killProcess.StartInfo.FileName = "wmic";
      killProcess.StartInfo.CreateNoWindow = true;
      killProcess.StartInfo.ErrorDialog = false;

      killProcess.StartInfo.Arguments = "process where (processid=\"" + (uint)slaveMinerProcess0Id + "\" and parentprocessid=\"" + Process.GetCurrentProcess().Id + "\") call terminate";

      killProcess.Start();
      killProcess.WaitForExit();
      killProcess.Close();
      killProcess.Dispose();
      killProcess = null;
     }

     if (slaveMinerProcess0 != null) slaveMinerProcess0.WaitForExit();

     GC.Collect();
    }
   }
   catch { }

   try
   {
    if (slaveMinerProcess1Id != -1)
    {
     if (options.direct_order == 1)
     {
      Process killProcess = new Process();
      killProcess.StartInfo.UseShellExecute = false;
      killProcess.StartInfo.FileName = "wmic";
      killProcess.StartInfo.CreateNoWindow = true;
      killProcess.StartInfo.ErrorDialog = false;

      killProcess.StartInfo.Arguments = "process where (processid=\"" + (uint)slaveMinerProcess1Id + "\" and parentprocessid=\"" + Process.GetCurrentProcess().Id + "\") call terminate";

      killProcess.Start();
      killProcess.WaitForExit();
      killProcess.Close();
      killProcess.Dispose();
      killProcess = null;

      KillAllProcessesSpawnedBy(slaveMinerProcess1StartTime, (uint)slaveMinerProcess1Id, slaveMinerProcess1DeathTime);
     }
     else
     {
      KillAllProcessesSpawnedBy(slaveMinerProcess1StartTime, (uint)slaveMinerProcess1Id, slaveMinerProcess1DeathTime);

      Process killProcess = new Process();
      killProcess.StartInfo.UseShellExecute = false;
      killProcess.StartInfo.FileName = "wmic";
      killProcess.StartInfo.CreateNoWindow = true;
      killProcess.StartInfo.ErrorDialog = false;

      killProcess.StartInfo.Arguments = "process where (processid=\"" + (uint)slaveMinerProcess1Id + "\" and parentprocessid=\"" + Process.GetCurrentProcess().Id + "\") call terminate";

      killProcess.Start();
      killProcess.WaitForExit();
      killProcess.Close();
      killProcess.Dispose();
      killProcess = null;
     }

     if (slaveMinerProcess1 != null) slaveMinerProcess1.WaitForExit();

     GC.Collect();
    }
   }
   catch { }

   if (waitAfterKill) Thread.Sleep(3000);

   TaskKill();

   killIsActive = false;

   try
   {
    if (mainThreadStart != null) mainThreadStart.Join();
    if (slave0ThreadStart != null) slave0ThreadStart.Join();
    if (slave1ThreadStart != null) slave1ThreadStart.Join();

    mainThreadStart = null;
    slave0ThreadStart = null;
    slave1ThreadStart = null;
   }
   catch { }
  }

  [SecurityCritical]
  static void criticalEvent(object sendingProcess)
  {
   lock (_lockObj)
   {
    try
    {
     if (sendingProcess != null)
     {
      Process pr = (Process)sendingProcess;
      int pid = pr.Id;

      Console.ForegroundColor = ConsoleColor.Magenta;
      if (Program.ru_lang)
       Console.WriteLine("Принудительный перезапуск добычи");
      else
       Console.WriteLine("Forced miner restart");
      Console.ForegroundColor = ConsoleColor.White;

      if (options.direct_order == 1)
      {
       Process killProcess = new Process();
       killProcess.StartInfo.UseShellExecute = false;
       killProcess.StartInfo.FileName = "wmic";
       killProcess.StartInfo.CreateNoWindow = true;
       killProcess.StartInfo.ErrorDialog = false;

       killProcess.StartInfo.Arguments = "process where (processid=\"" + pid + "\" and parentprocessid=\"" + Process.GetCurrentProcess().Id + "\") call terminate";

       killProcess.Start();
       killProcess.WaitForExit();
       killProcess.Close();
       killProcess.Dispose();
       killProcess = null;

       if (slaveMinerProcess0 != null)
       {
        if (slaveMinerProcess0.Id == pid)
         KillAllProcessesSpawnedBy(slaveMinerProcess0StartTime, (uint)pid, slaveMinerProcess0DeathTime);
       }

       if (slaveMinerProcess1 != null)
       {
        if (slaveMinerProcess1.Id == pid)
         KillAllProcessesSpawnedBy(slaveMinerProcess1StartTime, (uint)pid, slaveMinerProcess1DeathTime);
       }

       if (masterMinerProcess != null)
       {
        if (masterMinerProcess.Id == pid)
         KillAllProcessesSpawnedBy(masterMinerProcessStartTime, (uint)pid, masterMinerProcessDeathTime);
       }
      }
      else
      {
       if (slaveMinerProcess0 != null)
       {
        if (slaveMinerProcess0.Id == pid)
         KillAllProcessesSpawnedBy(slaveMinerProcess0StartTime, (uint)pid, slaveMinerProcess0DeathTime);
       }

       if (slaveMinerProcess1 != null)
       {
        if (slaveMinerProcess1.Id == pid)
         KillAllProcessesSpawnedBy(slaveMinerProcess1StartTime, (uint)pid, slaveMinerProcess1DeathTime);
       }

       if (masterMinerProcess != null)
       {
        if (masterMinerProcess.Id == pid)
         KillAllProcessesSpawnedBy(masterMinerProcessStartTime, (uint)pid, masterMinerProcessDeathTime);
       }

       Process killProcess = new Process();
       killProcess.StartInfo.UseShellExecute = false;
       killProcess.StartInfo.FileName = "wmic";
       killProcess.StartInfo.CreateNoWindow = true;
       killProcess.StartInfo.ErrorDialog = false;

       killProcess.StartInfo.Arguments = "process where (processid=\"" + pid + "\" and parentprocessid=\"" + Process.GetCurrentProcess().Id + "\") call terminate";

       killProcess.Start();
       killProcess.WaitForExit();
       killProcess.Close();
       killProcess.Dispose();
       killProcess = null;
      }

      if (pr != null) pr.WaitForExit();

      GC.Collect();
     }
     else
     {
      Console.ForegroundColor = ConsoleColor.Magenta;
      if (Program.ru_lang)
       Console.WriteLine("Принудительный перезапуск добычи");
      else
       Console.WriteLine("Forced miner restart");
      Console.ForegroundColor = ConsoleColor.White;

      if (slaveMinerProcess0Id != -1)
       KillAllProcessesSpawnedBy(slaveMinerProcess0StartTime, (uint)slaveMinerProcess0Id, slaveMinerProcess0DeathTime);

      if (slaveMinerProcess1Id != -1)
       KillAllProcessesSpawnedBy(slaveMinerProcess1StartTime, (uint)slaveMinerProcess1Id, slaveMinerProcess1DeathTime);

      if (masterMinerProcessId != -1)
       KillAllProcessesSpawnedBy(masterMinerProcessStartTime, (uint)masterMinerProcessId, masterMinerProcessDeathTime);
     }
    }
    catch { }
   }
  }

  [SecurityCritical]
  static void ParseMessage(object sendingProcess, string message)
  {
   lock (_lockObj)
   {
    // HTML codes cleaner
    message = new Regex(@"\x1B\[[^@-~]*[@-~]").Replace(message, String.Empty);

    // message filter
    {
     message = message.Filter(new List<string>() { "Putin khuylo! " });
    }

    if (message.Contains("new job") || (message.Contains("diff") && !message.Contains("[B/A/T]") && !message.Contains("accepted")))
    {
     Console.ForegroundColor = ConsoleColor.DarkCyan;
     Console.WriteLine(message);
     Console.ForegroundColor = ConsoleColor.White;
    }
    else if (message.Contains("profit:"))
    {
     Console.ForegroundColor = ConsoleColor.Red;
     Console.WriteLine(message);
     Console.ForegroundColor = ConsoleColor.White;
    }
    else if (message.Contains("Total Speed") || message.Contains("Total:"))
    {
     Console.ForegroundColor = ConsoleColor.DarkYellow;
     Console.WriteLine(message);
     Console.ForegroundColor = ConsoleColor.White;
    }
    else if (message.Contains("TCP Client"))
    {
     Console.ForegroundColor = ConsoleColor.Magenta;
     Console.WriteLine(message);
     Console.ForegroundColor = ConsoleColor.White;

     if (message.Contains("Receiving message timeout"))
     {
      criticalEvent(sendingProcess);

      Thread.Sleep(10000);
     }
    } // xmrig and NIM
    else if ((message.Contains("speed 10s/60s/15m 0.0 0.0") || message.Contains("speed 10s/60s/15m 0 0")) || message.Contains("GPU#0 0 H/s") || message.Contains("GPU#1 0 H/s") || message.Contains("GPU#2 0 H/s") || message.Contains("GPU#3 0 H/s") || message.Contains("GPU#4 0 H/s") || message.Contains("GPU#5 0 H/s") || message.Contains("GPU#6 0 H/s") || message.Contains("GPU#7 0 H/s") || message.Contains("GPU#8 0 H/s") || message.Contains("GPU#9 0 H/s"))
    {
     Console.ForegroundColor = ConsoleColor.Magenta;
     Console.WriteLine(message);
     Console.ForegroundColor = ConsoleColor.White;

     criticalEvent(sendingProcess);
    }
    else if (options.ignore_no_active_pools_message == 0 && message.Contains("no active pools, stop mining"))
    {
     Console.ForegroundColor = ConsoleColor.Magenta;
     Console.WriteLine(message);
     Console.ForegroundColor = ConsoleColor.White;

     criticalEvent(sendingProcess);
    }
    /*
        else if (message.Contains("Duplicate share submitted")) // Rigel old bug
        {
         Console.ForegroundColor = ConsoleColor.Magenta;
         Console.WriteLine(message);
         Console.ForegroundColor = ConsoleColor.White;

         criticalEvent(sendingProcess);
        }
        else if (message.Contains("Share rejected: Invalid share Err#414")) // OneZero old bug (DNX)
        {
         Console.ForegroundColor = ConsoleColor.Magenta;
         Console.WriteLine(message);
         Console.ForegroundColor = ConsoleColor.White;

         criticalEvent(sendingProcess);
        }
    */
    else if (message.Contains("PL0: [FAILED]") || message.Contains("Mining will be paused until connection to the devfee pool can be established")) // SRBMiner-Multi bugs
    {
     Console.ForegroundColor = ConsoleColor.Magenta;
     Console.WriteLine(message);
     Console.ForegroundColor = ConsoleColor.White;

     criticalEvent(sendingProcess);
    }
    else if (message.ToLower().Contains("mallob") && message.ToLower().Contains("error")) // SRBMiner-Multi bugs
    {
     if (masterMinerProcess != null || slaveMinerProcess0 != null || slaveMinerProcess1 != null)
     {
      DateTime n = DateTime.Now;
      DateTime m = n;
      if (masterMinerProcess != null)
       m = masterMinerProcessStartTime;
      else if (slaveMinerProcess0 != null)
       m = slaveMinerProcess0StartTime;
      else if (slaveMinerProcess1 != null)
       m = slaveMinerProcess1StartTime;

      if ((n - m).TotalMinutes > 5.0)
      {
       Console.ForegroundColor = ConsoleColor.Magenta;
       Console.WriteLine(message);
       Console.ForegroundColor = ConsoleColor.White;

       criticalEvent(sendingProcess);
      }
      else
      {
       Console.ForegroundColor = ConsoleColor.Magenta;
       Console.WriteLine(message);
       Console.ForegroundColor = ConsoleColor.White;
      }
     }
    }
    else if (message.Contains("DNS error: \"temporary failure\"") || message.Contains("DNS error: \"unknown node or service\"")) // глобальная ошибка DNS, скорее всего ничего не будет добываться ничем
    {
     Console.ForegroundColor = ConsoleColor.Magenta;
     Console.WriteLine(message);
     Console.ForegroundColor = ConsoleColor.White;

     Thread.Sleep(10000);
    }
    else if (mainThread_enabled && (message.Contains("Invalid wallet address") || message.Contains("Connection Error:") || message.Contains("THREAD #0 COMPUTE ERROR") || message.Contains("connect error: \"connection refused\""))) // Основная монета
    {
     Console.ForegroundColor = ConsoleColor.Magenta;
     Console.WriteLine(message);
     Console.ForegroundColor = ConsoleColor.White;

     lock (timeOfLatestBan_SyncObject)
     {
      if (timeOfLatestBan == null)
      {
       timeOfLatestBan = (object)DateTime.Now;

       runBan();

       criticalEvent(sendingProcess);
      }
      else
      {
       DateTime last_event = (DateTime)timeOfLatestBan;
       DateTime now_event = DateTime.Now;

       if ((now_event - last_event).TotalSeconds < 30)
       {
        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine("Слишком быстро пытаемся выдать новый бан, игнор");
        Console.ForegroundColor = ConsoleColor.White;
       }
       else
       {
        timeOfLatestBan = (object)now_event;

        runBan();

        criticalEvent(sendingProcess);
       }
      }
     }
    }
    else if (message.Contains("First switch to dev pool. First time is based on a random threshold"))
    {
     Console.ForegroundColor = ConsoleColor.Magenta;
     Console.WriteLine(message);
     Console.ForegroundColor = ConsoleColor.White;

     criticalEvent(sendingProcess);
    }
    else if (message.Contains("OFFLINE") || message.Contains("Existing CudaSolver or OclSolver processes running"))
    {
     Console.ForegroundColor = ConsoleColor.Magenta;
     Console.WriteLine(message);
     Console.ForegroundColor = ConsoleColor.White;

     criticalEvent(sendingProcess);
    }
    else if (message.Contains("karlsen_miner::miner] Closing miner") || message.Contains("karlsen_miner::miner] Workers stalled or crashed"))
    {
     Console.ForegroundColor = ConsoleColor.Magenta;
     Console.WriteLine(message);
     Console.ForegroundColor = ConsoleColor.White;

     criticalEvent(sendingProcess);
    }
    else if (message.Contains("Stratum: No shares") || message.ToLower(CultureInfo.CurrentCulture).Contains("reconnect"))
    {
     Console.ForegroundColor = ConsoleColor.Red;
     Console.WriteLine(message);
     Console.ForegroundColor = ConsoleColor.White;
    }
    // xmrig, wildrig, SRB, OneZero and karlsen_miner
    else if (message.Contains("speed 10s/60s/15m") || message.Contains("hashrate: 10s:") || (message.Contains("15m:") && message.Contains("Ignored:")) || message.Contains("Run time") || message.Contains("Uptime:") || message.Contains("karlsen_miner::miner] Current hashrate is"))
    {
     if (mainThread_enabled)
     {
      lock (timeOfLatestAccepted_SyncObject)
      {
       DateTime last_event_cache = ((timeOfLatestAccepted == null) ? DateTime.Now : (DateTime)timeOfLatestAccepted);
       DateTime now_event = DateTime.Now;

       double span = (now_event - last_event_cache).TotalSeconds;
       if (span > ((int)acceptedTimeOut))
       {
        if (options.ban_or_restart_no_shares_event == 0)
        {
         Console.ForegroundColor = ConsoleColor.Magenta;
         Console.WriteLine("Слишком долго нет ни одной принятой шары, пул идет в бан");
         Console.ForegroundColor = ConsoleColor.White;

         lock (timeOfLatestBan_SyncObject)
         {
          if (timeOfLatestBan == null)
          {
           timeOfLatestBan = (object)DateTime.Now;

           runBan();

           criticalEvent(sendingProcess);
          }
          else
          {
           DateTime last_event = (DateTime)timeOfLatestBan;

           if ((now_event - last_event).TotalSeconds < 30)
           {
            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine("Слишком быстро пытаемся выдать новый бан, игнор");
            Console.ForegroundColor = ConsoleColor.White;
           }
           else
           {
            timeOfLatestBan = (object)now_event;

            runBan();

            criticalEvent(sendingProcess);
           }
          }
         }
        }
        else
        {
         Console.ForegroundColor = ConsoleColor.Magenta;
         Console.WriteLine("Слишком долго нет ни одной принятой шары, перезапускаем майнер");
         Console.ForegroundColor = ConsoleColor.White;

         criticalEvent(sendingProcess);
        }
       }
       else
       {
        if (message.Contains("Run time") || message.Contains("Uptime:"))
         Console.WriteLine(message + Environment.NewLine + "                      Со времени последнего критического для добычи события/шары прошло '{0}' секунд", span.ToString("0.000").Replace(',', '.'));
        else if (message.Contains("15m:") && message.Contains("Ignored:"))
         Console.WriteLine(message + Environment.NewLine + " Со времени последнего критического для добычи события/шары прошло '{0}' секунд", span.ToString("0.000").Replace(',', '.'));
        else if (message.Contains("karlsen_miner::miner] Current hashrate is"))
         Console.WriteLine(message + Environment.NewLine + "                                                  Со времени последнего критического для добычи события/шары прошло '{0}' секунд", span.ToString("0.000").Replace(',', '.'));
        else
         Console.WriteLine(message + ", со времени последнего критического для добычи события/шары прошло '{0}' секунд", span.ToString("0.000").Replace(',', '.'));
       }
      }
     }
     else
     {
      Console.ForegroundColor = ConsoleColor.White;
      Console.WriteLine(message);
     }
    } // Проверка на accepted
    else if ((message.ToLower(CultureInfo.CurrentCulture).Contains("accepted") && !message.Contains("Accepted:")) || message.Contains("[ OK ]") || message.Contains("[ BLOCK ]") || (message.Contains("diff") && message.Contains("[B/A/T]")))
    {
     Console.ForegroundColor = ConsoleColor.DarkGreen;
     Console.WriteLine(message);
     Console.ForegroundColor = ConsoleColor.White;

     if (mainThread_enabled)
     {
      lock (timeOfLatestAccepted_SyncObject)
      {
       acceptedTimeOut = (object)(options.share_wait_timeout);

       timeOfLatestAccepted = (object)DateTime.Now;
      }
     }
    }
    else
    {
     Console.ForegroundColor = ConsoleColor.White;
     Console.WriteLine(message);
    }
   }
  }

  volatile static string[] config_lines;
  volatile static string[] exe_lines;

  volatile static Options options;

  static void RunOptions(Options o)
  {
   if (Program.ru_lang)
   {
    Console.WriteLine("Параметры запуска: ");

    Console.WriteLine("-k : Убить таблетку до запуска майнеров; значения: 0 или 1, по умолчанию 0");
    Console.WriteLine("-w : Запустить майнеры без внешних окон; значения: 0 или 1, по умолчанию 1");
    Console.WriteLine("-s : Поддержка антивотчдог-системы; если используете батник или ярлык, задавайте его имя также, как имя исполняемого файла; значения: 0 или 1, по умолчанию 1");
    Console.WriteLine("-o : Прямой порядок завершения процесса добычи; значения: 0 или 1, по умолчанию 1");
    Console.WriteLine("-e : Завершить работу внутреннего цикла после поломки майнера; значения: 0 или 1, по умолчанию 1");
    Console.WriteLine("-d : Использование dummy-майнера (с именем dummy.exe), по умолчанию запускается с параметрами '" + default_dummy_params + "', если иное не указано в пятой строчке конфигурационного файла; значения: 0 или 1, по умолчанию 0");
    Console.WriteLine("-p : Базовый период ожидания запуска майнера, по умолчанию " + Options.default_wait_timeout_value + " секунд");
    Console.WriteLine("-q : Базовый период ожидания первой шары, по умолчанию " + Options.default_share_wait_timeout_value + " секунд");
    Console.WriteLine("-i : Игнорировать сообщения вида 'no active pools'; значения: 0 или 1, по умолчанию 1");
    Console.WriteLine("-v : Время бана пула (минут); значения: по умолчанию 30 минут");
    Console.WriteLine("-m : Поведение при наступлении события \"долгое время нет шар\"; значения: 0 (бан) или 1 (рестарт майнера), по умолчанию 1" + Environment.NewLine + Environment.NewLine);

    Console.WriteLine("Пример: " + AppDomain.CurrentDomain.FriendlyName + " -k 0 -w 1 -s 1 -o 1 -e 1 -d -p " + Options.default_wait_timeout_value + " -q " + Options.default_share_wait_timeout_value + " -i 1 -v 30 -m 1");
   }
   else
   {
    Console.WriteLine("Parameters: ");

    Console.WriteLine("-k : Kill a pill before starting mining; variants: 0 or 1, default 0");
    Console.WriteLine("-w : Run without windows for external miners; variants: 0 or 1, default 1");
    Console.WriteLine("-s : Anti-watchdog support; if You use a bat-file or lnk-shortcut, set his name as well as the name of the executable file; variants: 0 or 1, default 1");
    Console.WriteLine("-o : Direct procedure for completing the mining process; variants: 0 or 1, default 1");
    Console.WriteLine("-e : Quit internal cycle after miner crash; variants: 0 or 1, default 1");
    Console.WriteLine("-d : Using a dummy-miner (named as dummy.exe), by default it starts with parameters '" + default_dummy_params + "', unless otherwise specified in the fifth line of the configuration file; variants: 0 or 1, default 0");
    Console.WriteLine("-p : Base waiting period for the launch of the miner, default " + Options.default_wait_timeout_value + " seconds");
    Console.WriteLine("-q : Base waiting period for the first share, default " + Options.default_share_wait_timeout_value + " seconds");
    Console.WriteLine("-i : Ignore messages like 'no active pools'; variants: 0 or 1, default 1");
    Console.WriteLine("-v : Ban-time for the pool (minutes), default 30 minutes");
    Console.WriteLine("-m : Behavior upon occurrence of an event \"For a long time there is no shares\"; variants: 0 (ban) or 1 (miner restart), by default 1" + Environment.NewLine + Environment.NewLine);

    Console.WriteLine("Example: " + AppDomain.CurrentDomain.FriendlyName + " -k 0 -w 1 -s 1 -o 1 -e 1 -d -p " + Options.default_wait_timeout_value + " -q " + Options.default_share_wait_timeout_value + " -i 1 -v 30 -m 1");
   }
   Console.WriteLine();

   options = o;

   bool badSettings = false;
   if (options.kill_pill < 0 || options.kill_pill > 1) badSettings = true;
   else if (options.without_external_windows < 0 || options.without_external_windows > 1) badSettings = true;
   else if (options.with_antiwatchdog < 0 || options.with_antiwatchdog > 1) badSettings = true;
   else if (options.direct_order < 0 || options.direct_order > 1) badSettings = true;
   else if (options.exit_after_miner_fail < 0 || options.exit_after_miner_fail > 1) badSettings = true;
   else if (options.use_dummy_miner < 0 || options.use_dummy_miner > 1) badSettings = true;
   else if (options.wait_timeout < 0 || options.wait_timeout > 1000000) badSettings = true;
   else if (options.share_wait_timeout < 0 || options.share_wait_timeout > 1000000) badSettings = true;
   else if (options.ignore_no_active_pools_message < 0 || options.ignore_no_active_pools_message > 1) badSettings = true;
   else if (options.ban_timeout < 0 || options.ban_timeout > 1000000) badSettings = true;
   else if (options.ban_or_restart_no_shares_event < 0 || options.ban_or_restart_no_shares_event > 1) badSettings = true;

   if (options.without_external_windows == 0)
   {
    if (Program.ru_lang)
     Console.WriteLine("Предупреждение: режим перехвата сообщений с обработкой не активируется при внешнем окне майнера");
    else
     Console.WriteLine("Warning: processing messages is not activated with external miner window");

    Console.WriteLine();
   }
   else
   {
    if (Program.ru_lang)
     Console.WriteLine("Предупреждение: если Вы используете SRBMiner-Multi, паузы между порциями сообщений могут достигать 2 и более минут, базовый период ожидания запуска майнера должен быть больше, иначе будут возникать коллизии на обработке информации");
    else
     Console.WriteLine("Warning: if You use SRBMiner-Multi, the pauses between portions of messages can reach 2 or more minutes, the base period of waiting for the miner should be larger, otherwise conflicts will arise in the processing of information");

    Console.WriteLine();
   }

   if (Program.ru_lang)
    Console.WriteLine("Предупреждение: при запуске батников выключен мониторинг следующих процессов: \"OhGodAnETHlargementPill-r2.exe\", \"sleep\", \"timeout\", \"MSIAfterburner.exe\", \"curl\", \"tasklist\", \"find\", \"powershell\", \"start\", \"cd\" и \"taskkill\"" + Environment.NewLine);
   else
    Console.WriteLine("Warning: when starting bat-files, the monitoring of the following processes is turned off: \"OhGodAnETHlargementPill-r2.exe\", \"sleep\", \"timeout\"\"MSIAfterburner.exe\", \"curl\", \"tasklist\", \"find\", \"powershell\", \"start\", \"cd\" and \"taskkill\"" + Environment.NewLine);

   if (Program.ru_lang)
   {
    Console.WriteLine("Предупреждение: запуск PoolWatcher с привязкой к консоли (например, в случае запуска через \"start /B /W\") в редких случаях может привести к сокрытию основного окна из-за ошибки BEX64 у процесса conhost.exe (ошибка Windows), рекомендуется запускать PoolWatcher так, чтобы у него было собственное окно");
    Console.WriteLine("Полностью отключить ошибки BEX64 можно через \"bcdedit.exe /set {current} nx AlwaysOff\" (запускается с отключенным SecureBoot)" + Environment.NewLine);
   }
   else
   {
    Console.WriteLine("Warning: Poolwatcher attached launch to the console (for example, in the case of launch through \"start /B /W\") in rare cases can lead to hiding the main window due to the BEX64 error for the conhost.exe process (Windows error), it is recommended to run the PoolWatcher so that he has his own window");
    Console.WriteLine("You can completely turn off the BEX64 errors through \"bcdedit.exe /set {current} nx AlwaysOff\" (launched with disabled SecureBoot mode)" + Environment.NewLine);
   }


   if (Program.ru_lang)
   {
    Console.WriteLine("Для завершения добычи используйте закрытие основного окна с помощью крестика, вызов \"Ctrl+C\" или функцию \"Завершение дерева процессов\" в \"Диспетчере задач\"!");
    Console.WriteLine("\"Ctrl+C\" не работает в случае запуска PoolWatcher через \"start /B\"");
   }
   else
   {
    Console.WriteLine("To complete mining, use the closure of the main window with a cross, call \"Ctrl+C\" or function \"End the process tree\" in \"Task Manager\"!");
    Console.WriteLine("\"Ctrl+C\" does not work in the case of launch PoolWatcher through \"start /B\"");
   }

   if (badSettings)
   {
    if (Program.ru_lang)
     Console.WriteLine("Ошибочные параметры запуска");
    else
     Console.WriteLine("Wrong application settings");

    Environment.Exit(0);
   }

   Console.WriteLine();
  }

  static void HandleParseError(IEnumerable<Error> errs)
  {
   Environment.Exit(0);
  }

  static void PrintDonateWallets()
  {
   Console.WriteLine("BTC     bc1q2dpwy93qmmnq4utuu8ypmj3kqykm43p4wg5gpt");
  }

  [SecurityCritical]
  static void mainKiller()
  {
   main_cycle_enabled = false;

   waitAfterKill = false;

   KillMiner();

   evt_main.Set();
  }

  [DllImport("Kernel32")]
  private static extern bool SetConsoleCtrlHandler(EventHandler handler, bool add);

  private delegate bool EventHandler(CtrlType sig);
  static EventHandler _handler;

  enum CtrlType
  {
   CTRL_C_EVENT = 0,
   CTRL_BREAK_EVENT = 1,
   CTRL_CLOSE_EVENT = 2,
   CTRL_LOGOFF_EVENT = 5,
   CTRL_SHUTDOWN_EVENT = 6
  }

  private static bool Handler(CtrlType sig)
  {
   switch (sig)
   {
    case CtrlType.CTRL_C_EVENT:
    case CtrlType.CTRL_BREAK_EVENT:
    case CtrlType.CTRL_CLOSE_EVENT:
    case CtrlType.CTRL_LOGOFF_EVENT:
    case CtrlType.CTRL_SHUTDOWN_EVENT:
    default:
     {
      mainKiller();

      return false;
     }
   }
  }

  [HandleProcessCorruptedStateExceptions, SecurityCritical]
  static void Main(string[] args)
  {
#if TOPMOST
   {
    const int HWND_TOPMOST = -1;
    const int SWP_NOMOVE = 0x0002;
    const int SWP_NOSIZE = 0x0001;
    ConsoleWindow.NativeFunctions.SetWindowPos(Process.GetCurrentProcess().MainWindowHandle, new IntPtr(HWND_TOPMOST), 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);
   }
#endif

   try
   {
    Console.SetBufferSize(Console.BufferWidth, Int16.MaxValue - 1);
   }
   catch { }

   ConsoleWindow.QuickEditMode(false);

   start_task.Start();

   ru_lang = (CultureInfo.CurrentCulture.Name == "ru-RU");
   Parser.Default.ParseArguments<Options>(args).WithParsed(RunOptions).WithNotParsed(HandleParseError);

   WerAddExcludedApplication(AppDomain.CurrentDomain.FriendlyName, false);
   SetErrorMode(ErrorModes.SEM_NOGPFAULTERRORBOX | ErrorModes.SEM_FAILCRITICALERRORS);
   SetThreadErrorMode(ErrorModes.SEM_NOGPFAULTERRORBOX | ErrorModes.SEM_FAILCRITICALERRORS, out ErrorModes dummyErrorMode);

   AppDomain.CurrentDomain.UnhandledException += delegate { mainKiller(); };
   AppDomain.CurrentDomain.ProcessExit += delegate { mainKiller(); };
   Console.CancelKeyPress += delegate { mainKiller(); };

   _handler += new EventHandler(Handler);
   SetConsoleCtrlHandler(_handler, true);

   // DeleteMenu(GetSystemMenu(GetConsoleWindow(), false), 0xF060, 0x00000000); // отключение кнопки закрытия

   Version version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;

   string path_to_config_file = Path.ChangeExtension(System.AppDomain.CurrentDomain.FriendlyName, ".config");
   config_lines = new string[5];
   config_lines[3] = defaultPool + ":" + defaultPort;
   config_lines[4] = default_dummy_params;

   exe_lines = new string[3] { String.Empty, String.Empty, String.Empty };

   if (System.IO.File.Exists(path_to_config_file))
   {
    string[] config_lines_tmp = System.IO.File.ReadAllLines(path_to_config_file);
    for (int i = 0; i < config_lines.Length && i < config_lines_tmp.Length; i++)
    {
     if (i < 3)
     {
      if (String.IsNullOrEmpty(config_lines_tmp[i]))
       exe_lines[i] = String.Empty;
      else
      {
       config_lines_tmp[i] = config_lines_tmp[i].Trim();

       int index_of_empty_symbol = config_lines_tmp[i].IndexOf(' ');

       if (index_of_empty_symbol != -1)
       {
        int index_of_endofname_symbol = config_lines_tmp[i].IndexOf('"', 1);

        if (index_of_endofname_symbol != -1 && index_of_endofname_symbol > index_of_empty_symbol)
        {
         int index_of_empty_symbol_2 = config_lines_tmp[i].IndexOf(' ', index_of_endofname_symbol);

         if (index_of_empty_symbol_2 != -1)
         {
          exe_lines[i] = config_lines_tmp[i].Substring(0, index_of_endofname_symbol + 1).Replace("\"", "");

          if (config_lines_tmp[i].Length > exe_lines[i].Length)
           config_lines[i] = config_lines_tmp[i].Substring(index_of_endofname_symbol + 2);
          else
           config_lines[i] = String.Empty;
         }
         else
          exe_lines[i] = config_lines_tmp[i].Replace("\"", "");
        }
        else
        {
         exe_lines[i] = config_lines_tmp[i].Substring(0, index_of_empty_symbol).Replace("\"", ""); ;

         config_lines[i] = config_lines_tmp[i].Substring(index_of_empty_symbol + 1);
        }
       }
       else
        exe_lines[i] = config_lines_tmp[i].Replace("\"", "");
      }
     }
     else config_lines[i] = config_lines_tmp[i].Trim();
    }

    if (String.IsNullOrEmpty(config_lines[3])) config_lines[3] = defaultPool + ":" + defaultPort;
    else if (!config_lines[3].Contains(":")) config_lines[3] += ":" + defaultPort;
   }
   else
   {
    if (Program.ru_lang)
     Console.WriteLine("Файл '" + path_to_config_file + "' не найден!");
    else
     Console.WriteLine("File '" + path_to_config_file + "' not founded!");

    Environment.Exit(0);
   }

   if (Program.ru_lang)
   {
    Console.WriteLine("Версия следилки: " + version + Environment.NewLine);

    Console.WriteLine("Адреса для доната: ");
    PrintDonateWallets();
    Console.WriteLine();
   }
   else
   {
    Console.WriteLine("PoolWatcher version: " + version + Environment.NewLine);

    Console.WriteLine("Donate wallets: ");
    PrintDonateWallets();
    Console.WriteLine();
   }

   listener.IsBackground = true;
   listener.Start();

   try
   {
    if (Program.ru_lang)
     Console.WriteLine("Имена майнеров должны быть уникальные, сейчас запускаются следующие:");
    else
     Console.WriteLine("Miner names must be unique, the following are now starting: ");

    for (int i = 0; i < exe_lines.Length; i++) Console.WriteLine("\"" + exe_lines[i] + "\"");
    Console.WriteLine();

    bool first_start = true;

    UInt64 connect_errors = 0U;

    if (Program.ru_lang)
     Console.WriteLine("Проверяем подключение к {0}", config_lines[3]);
    else
     Console.WriteLine("Check connection to the {0}", config_lines[3]);

    while (main_cycle_enabled)
    {
     while (killIsActive) Thread.Sleep(250);

     if (connect_errors > 2 || waitAfterBan) // Если пул основной монеты недоступен или забанен
     {
      first_start = false;

      connect_errors = 0U;

      bool miners_killed = false;

      if (main_cycle_enabled && (slave0thread == false || slave1thread == false))
      {
       KillMiner();

       if (options.kill_pill == 1) killPill();

       slave0thread = true;
       if (!string.IsNullOrEmpty(exe_lines[1]))
       {
        while (slave0ThreadStart != null) Thread.Sleep(500);
        while (killIsActive) Thread.Sleep(250);

        slave0ThreadStart = new Thread((() =>
        {
         while (slave0thread && main_cycle_enabled)
         {
          try
          {
           if (System.IO.File.Exists(exe_lines[1]))
           {
            lock (listener_status)
            {
             listener_status = 1;

             if (Program.ru_lang)
              Console.WriteLine(Environment.NewLine + "Запуск майнера номер 2 (альтернативный)");
             else
              Console.WriteLine(Environment.NewLine + "Start of miner number 2 (alternative)");

             slaveMinerProcess0DeathTime = DateTime.MaxValue;

             if (exe_lines[1].EndsWith(".lnk", StringComparison.CurrentCulture))
             {
              IWshShortcut link = (IWshShortcut)shell.CreateShortcut(exe_lines[1]);

              if (link.TargetPath.EndsWith(".bat", StringComparison.CurrentCulture))
               WerAddExcludedApplication(Path.ChangeExtension(link.TargetPath, ".exe"), false);
              WerAddExcludedApplication(link.TargetPath, false);

              if (options.without_external_windows == 1)
               CallProcess(link.TargetPath.EndsWith(".bat", StringComparison.CurrentCulture), "\"" + link.TargetPath + "\"" + " " + config_lines[1], link.WorkingDirectory, CreateProcessFlags.CREATE_NO_WINDOW, ProcessVariant.Slave0, false);
              else
               CallProcess(link.TargetPath.EndsWith(".bat", StringComparison.CurrentCulture), "\"" + link.TargetPath + "\"" + " " + config_lines[1], link.WorkingDirectory, CreateProcessFlags.CREATE_NEW_CONSOLE, ProcessVariant.Slave0, false);
             }
             else
             {
              if ((new FileInfo(exe_lines[1])).FullName.EndsWith(".bat", StringComparison.CurrentCulture))
               WerAddExcludedApplication(Path.ChangeExtension((new FileInfo(exe_lines[1])).FullName, ".exe"), false);
              WerAddExcludedApplication((new FileInfo(exe_lines[1])).FullName, false);

              if (options.without_external_windows == 1)
               CallProcess((new FileInfo(exe_lines[1])).FullName.EndsWith(".bat", StringComparison.CurrentCulture), "\"" + (new FileInfo(exe_lines[1])).FullName + "\"" + " " + config_lines[1], (new FileInfo(exe_lines[1])).DirectoryName, CreateProcessFlags.CREATE_NO_WINDOW, ProcessVariant.Slave0, false);
              else
               CallProcess((new FileInfo(exe_lines[1])).FullName.EndsWith(".bat", StringComparison.CurrentCulture), "\"" + (new FileInfo(exe_lines[1])).FullName + "\"" + " " + config_lines[1], (new FileInfo(exe_lines[1])).DirectoryName, CreateProcessFlags.CREATE_NEW_CONSOLE, ProcessVariant.Slave0, false);
             }

             slave0MinerProcessCounter++;
            }
           }
           else
           {
            if (Program.ru_lang)
             Console.WriteLine("Не могу найти файл '" + exe_lines[1] + "', проверь путь!");
            else
             Console.WriteLine("Can't find file '" + exe_lines[1] + "', check path!");

            Thread.Sleep(3000);
           }
          }
          catch { }

          while (killIsActive) Thread.Sleep(250);

          if (options.with_antiwatchdog == 1) TaskKillByArg(exe_lines[1], 2);
          if (options.exit_after_miner_fail == 1)
          {
           slave0thread = false;

           break;
          }
         }
        }));

        if (main_cycle_enabled)
        {
         slave0ThreadStart.IsBackground = true;

         slave0ThreadStart.Start();
        }
       }

       slave1thread = true;
       if (!string.IsNullOrEmpty(exe_lines[2]))
       {
        while (slave1ThreadStart != null) Thread.Sleep(500);
        while (killIsActive) Thread.Sleep(250);

        slave1ThreadStart = new Thread((() =>
        {
         while (slave1thread && main_cycle_enabled)
         {
          try
          {
           if (System.IO.File.Exists(exe_lines[2]))
           {
            lock (listener_status)
            {
             listener_status = 1;

             if (Program.ru_lang)
              Console.WriteLine(Environment.NewLine + "Запуск майнера номер 3 (альтернативный)");
             else
              Console.WriteLine(Environment.NewLine + "Start of miner number 3 (alternative)");

             slaveMinerProcess1DeathTime = DateTime.MaxValue;

             if (exe_lines[2].EndsWith(".lnk", StringComparison.CurrentCulture))
             {
              IWshShortcut link = (IWshShortcut)shell.CreateShortcut(exe_lines[2]);

              if (link.TargetPath.EndsWith(".bat", StringComparison.CurrentCulture))
               WerAddExcludedApplication(Path.ChangeExtension(link.TargetPath, ".exe"), false);
              WerAddExcludedApplication(link.TargetPath, false);

              if (options.without_external_windows == 1)
               CallProcess(link.TargetPath.EndsWith(".bat", StringComparison.CurrentCulture), "\"" + link.TargetPath + "\"" + " " + config_lines[2], link.WorkingDirectory, CreateProcessFlags.CREATE_NO_WINDOW, ProcessVariant.Slave1, false);
              else
               CallProcess(link.TargetPath.EndsWith(".bat", StringComparison.CurrentCulture), "\"" + link.TargetPath + "\"" + " " + config_lines[2], link.WorkingDirectory, CreateProcessFlags.CREATE_NEW_CONSOLE, ProcessVariant.Slave1, false);
             }
             else
             {
              if ((new FileInfo(exe_lines[2])).FullName.EndsWith(".bat", StringComparison.CurrentCulture))
               WerAddExcludedApplication(Path.ChangeExtension((new FileInfo(exe_lines[2])).FullName, ".exe"), false);
              WerAddExcludedApplication((new FileInfo(exe_lines[2])).FullName, false);

              if (options.without_external_windows == 1)
               CallProcess((new FileInfo(exe_lines[2])).FullName.EndsWith(".bat", StringComparison.CurrentCulture), "\"" + (new FileInfo(exe_lines[2])).FullName + "\"" + " " + config_lines[2], (new FileInfo(exe_lines[2])).DirectoryName, CreateProcessFlags.CREATE_NO_WINDOW, ProcessVariant.Slave1, false);
              else
               CallProcess((new FileInfo(exe_lines[2])).FullName.EndsWith(".bat", StringComparison.CurrentCulture), "\"" + (new FileInfo(exe_lines[2])).FullName + "\"" + " " + config_lines[2], (new FileInfo(exe_lines[2])).DirectoryName, CreateProcessFlags.CREATE_NEW_CONSOLE, ProcessVariant.Slave1, false);
             }

             slave1MinerProcessCounter++;
            }
           }
           else
           {
            if (Program.ru_lang)
             Console.WriteLine("Не могу найти файл '" + exe_lines[2] + "', проверь путь!");
            else
             Console.WriteLine("Can't find file '" + exe_lines[2] + "', check path!");

            Thread.Sleep(3000);
           }
          }
          catch { }

          while (killIsActive) Thread.Sleep(250);

          if (options.with_antiwatchdog == 1) TaskKillByArg(exe_lines[2], 3);
          if (options.exit_after_miner_fail == 1)
          {
           slave1thread = false;

           break;
          }
         }
        }));

        if (main_cycle_enabled)
        {
         slave1ThreadStart.IsBackground = true;

         slave1ThreadStart.Start();
        }
       }
      }
      else if (main_cycle_enabled && (slave0thread == true || slave1thread == true))
      {
       if (slave0thread == true && slaveMinerProcess0 != null)
       {
        try
        {
         if (!slaveMinerProcess0.Responding)
         {
          if (Program.ru_lang)
           Console.WriteLine("Процесс '" + exe_lines[1] + "' перестал отвечать, ждем до 25 секунд и завершаем текущий цикл");
          else
           Console.WriteLine("The process '" + exe_lines[1] + "' has stopped answering, we are waiting for up to 25 seconds and complete the current cycle");

          UInt64 cur_slave0MinerProcessCounter = slave0MinerProcessCounter;

          for (int _t_temp = 0; _t_temp < 50; _t_temp++)
          {
           if (main_cycle_enabled)
            Thread.Sleep(500);
           else break;
          }

          if (main_cycle_enabled && cur_slave0MinerProcessCounter == slave0MinerProcessCounter && slaveMinerProcess0 != null)
          {
           if (!slaveMinerProcess0.Responding)
           {
            KillMiner();

            miners_killed = true;
           }
          }
         }
        }
        catch { }
       }

       if (miners_killed == false && slave1thread == true && slaveMinerProcess1 != null)
       {
        try
        {
         if (!slaveMinerProcess1.Responding)
         {
          if (Program.ru_lang)
           Console.WriteLine("Процесс '" + exe_lines[2] + "' перестал отвечать, ждем до 25 секунд и завершаем текущий цикл");
          else
           Console.WriteLine("The process '" + exe_lines[2] + "' has stopped answering, we are waiting for up to 25 seconds and complete the current cycle");

          UInt64 cur_slave1MinerProcessCounter = slave1MinerProcessCounter;

          for (int _t_temp = 0; _t_temp < 50; _t_temp++)
          {
           if (main_cycle_enabled)
            Thread.Sleep(500);
           else break;
          }

          if (main_cycle_enabled && cur_slave1MinerProcessCounter == slave1MinerProcessCounter && slaveMinerProcess1 != null)
          {
           if (!slaveMinerProcess1.Responding)
           {
            KillMiner();

            miners_killed = true;
           }
          }
         }
        }
        catch { }
       }
      }

      if (miners_killed == false && main_cycle_enabled)
      {
       sleep_timeout = Options.default_sleep_timeout;
       evt.Set();
       evt_main.WaitOne();
      }
     }
     else
     {
      try
      {
       connect_errors++;

       string[] pool_port = config_lines[3].Split(':');

       IPAddress addr;
       if (pool_port.Length > 0)
       {
        bool isNormalIP = IPAddress.TryParse(pool_port[0], out addr);

        if (!isNormalIP)
        {
         addr = Dns.GetHostEntry(pool_port[0]).AddressList.First(taddr => taddr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
        }
       }
       else
       {
        addr = Dns.GetHostEntry(defaultPool).AddressList.First(taddr => taddr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
       }

       int try_pool_port = defaultPort;
       if (pool_port.Length > 1)
       {
        try
        {
         try_pool_port = Convert.ToInt32(pool_port[1], null);
        }
        catch
        {
         if (Program.ru_lang)
          Console.WriteLine("Не удалось интерпретировать указанный порт, используется порт по умолчанию, " + defaultPort);
         else
          Console.WriteLine("Failed to interpret the specified port, default port " + defaultPort + " is used");
        }
       }

       IPEndPoint endpt = new IPEndPoint(addr, try_pool_port);

       bool mainPoolIsOk = false;
       Socket tempSocket = new Socket(endpt.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
       Thread pingThread = new Thread((() =>
       {
        try
        {
         tempSocket.Connect(endpt);

         mainPoolIsOk = true;
        }
        catch { }
       }));

       pingThread.IsBackground = true;
       pingThread.Start();

       for (int u = 0; u < 8; u++)
       {
        Thread.Sleep(500);

        if (mainPoolIsOk) { tempSocket.Disconnect(false); break; }
       }

       if (!mainPoolIsOk)
       {
        try
        {
         tempSocket.Dispose();
        }
        catch { }

        throw new SocketException();
       }

       tempSocket.Dispose();
       tempSocket = null;
       GC.Collect();

       if (options.use_dummy_miner == 1)
       {
        if (System.IO.File.Exists("dummy.exe"))
        {
         dummy_status_ok = false;

         ProcessStartInfo si1 = new ProcessStartInfo("dummy.exe", config_lines[4]);
         si1.CreateNoWindow = true;
         si1.RedirectStandardOutput = true;
         si1.RedirectStandardError = true;
         si1.WindowStyle = ProcessWindowStyle.Hidden;
         si1.UseShellExecute = false;
         Process pr1 = Process.Start(si1);
         pr1.OutputDataReceived += Pr1_DataReceived;
         pr1.ErrorDataReceived += Pr1_DataReceived;
         pr1.BeginOutputReadLine();
         pr1.BeginErrorReadLine();
         pr1.WaitForExit();
         pr1.Close();

         if (dummy_status_ok == false)
         {
          Console.WriteLine("Dummy-майнер отрапортовал о нарушении протокола работы с пулом основной монеты");

          runBan();

          throw new SocketException();
         }
        }
        else
        {
         Console.WriteLine("Dummy-майнер не найден");

         dummy_status_ok = true;
        }
       }

       connect_errors = 0;

       bool miners_killed = false;

       first_start = false;

       if (main_cycle_enabled && mainThread_enabled == false)
       {
        KillMiner();

        if (options.kill_pill == 1) killPill();

        mainThread_enabled = true;
        if (!string.IsNullOrEmpty(exe_lines[0]))
        {
         while (mainThreadStart != null) Thread.Sleep(500);
         while (killIsActive) Thread.Sleep(250);

         mainThreadStart = new Thread((() =>
         {
          while (mainThread_enabled && main_cycle_enabled)
          {
           try
           {
            if (System.IO.File.Exists(exe_lines[0]))
            {
             lock (listener_status)
             {
              listener_status = 1;

              if (Program.ru_lang)
               Console.WriteLine(Environment.NewLine + "Запуск майнера номер 1 (основной)");
              else
               Console.WriteLine(Environment.NewLine + "Start of miner number 1 (base)");

              masterMinerProcessDeathTime = DateTime.MaxValue;

              if (exe_lines[0].EndsWith(".lnk", StringComparison.CurrentCulture))
              {
               IWshShortcut link = (IWshShortcut)shell.CreateShortcut(exe_lines[0]);

               if (link.TargetPath.EndsWith(".bat", StringComparison.CurrentCulture))
                WerAddExcludedApplication(Path.ChangeExtension(link.TargetPath, ".exe"), false);
               WerAddExcludedApplication(link.TargetPath, false);

               if (options.without_external_windows == 1)
                CallProcess(link.TargetPath.EndsWith(".bat", StringComparison.CurrentCulture), "\"" + link.TargetPath + "\"" + " " + config_lines[0], link.WorkingDirectory, CreateProcessFlags.CREATE_NO_WINDOW, ProcessVariant.Master, false);
               else
                CallProcess(link.TargetPath.EndsWith(".bat", StringComparison.CurrentCulture), "\"" + link.TargetPath + "\"" + " " + config_lines[0], link.WorkingDirectory, CreateProcessFlags.CREATE_NEW_CONSOLE, ProcessVariant.Master, false);
              }
              else
              {
               if ((new FileInfo(exe_lines[0])).FullName.EndsWith(".bat", StringComparison.CurrentCulture))
                WerAddExcludedApplication(Path.ChangeExtension((new FileInfo(exe_lines[0])).FullName, ".exe"), false);
               WerAddExcludedApplication((new FileInfo(exe_lines[0])).FullName, false);

               if (options.without_external_windows == 1)
                CallProcess((new FileInfo(exe_lines[0])).FullName.EndsWith(".bat", StringComparison.CurrentCulture), "\"" + (new FileInfo(exe_lines[0])).FullName + "\"" + " " + config_lines[0], (new FileInfo(exe_lines[0])).DirectoryName, CreateProcessFlags.CREATE_NO_WINDOW, ProcessVariant.Master, false);
               else
                CallProcess((new FileInfo(exe_lines[0])).FullName.EndsWith(".bat", StringComparison.CurrentCulture), "\"" + (new FileInfo(exe_lines[0])).FullName + "\"" + " " + config_lines[0], (new FileInfo(exe_lines[0])).DirectoryName, CreateProcessFlags.CREATE_NEW_CONSOLE, ProcessVariant.Master, false);
              }

              masterMinerProcessCounter++;
             }
            }
            else
            {
             if (Program.ru_lang)
              Console.WriteLine("Не могу найти файл '" + exe_lines[0] + "', проверь путь!");
             else
              Console.WriteLine("Can't find file '" + exe_lines[0] + "', check path!");

             Thread.Sleep(3000);
            }
           }
           catch { }

           while (killIsActive) Thread.Sleep(250);

           if (options.with_antiwatchdog == 1) TaskKillByArg(exe_lines[0], 1);
           if (options.exit_after_miner_fail == 1 || waitAfterBan == true)
           {
            mainThread_enabled = false;

            break;
           }
          }
         }));

         if (main_cycle_enabled)
         {
          mainThreadStart.IsBackground = true;

          mainThreadStart.Start();
         }
        }
       }
       else if (main_cycle_enabled)
       {
        if (masterMinerProcess != null)
        {
         try
         {
          if (!masterMinerProcess.Responding)
          {
           if (Program.ru_lang)
            Console.WriteLine("Процесс '" + exe_lines[0] + "' перестал отвечать, ждем до 25 секунд и завершаем текущий цикл");
           else
            Console.WriteLine("The process '" + exe_lines[0] + "' has stopped answering, we are waiting for up to 25 seconds and complete the current cycle");

           UInt64 cur_masterMinerProcessCounter = masterMinerProcessCounter;

           for (int _t_temp = 0; _t_temp < 50; _t_temp++)
           {
            if (main_cycle_enabled)
             Thread.Sleep(500);
            else break;
           }

           if (main_cycle_enabled && cur_masterMinerProcessCounter == masterMinerProcessCounter && masterMinerProcess != null)
           {
            if (!masterMinerProcess.Responding)
            {
             KillMiner();

             miners_killed = true;
            }
           }
          }
         }
         catch { }
        }
       }

       if (miners_killed == false && main_cycle_enabled)
       {
        sleep_timeout = Options.default_sleep_timeout;
        evt.Set();
        evt_main.WaitOne();
       }
      }
      catch (SocketException)
      {
       if (main_cycle_enabled)
       {
        if (first_start)
        {
         sleep_timeout = 4000;
         evt.Set();
         evt_main.WaitOne();
        }
        else
        {
         sleep_timeout = 60000;
         evt.Set();
         evt_main.WaitOne();
        }
       }
      }
     }
    }

    while (killIsActive) Thread.Sleep(500);

    try
    {
     if (mainThreadStart != null) mainThreadStart.Join();
     if (slave0ThreadStart != null) slave0ThreadStart.Join();
     if (slave1ThreadStart != null) slave1ThreadStart.Join();

     mainThreadStart = null;
     slave0ThreadStart = null;
     slave1ThreadStart = null;
    }
    catch { }
   }
   catch (Exception ex)
   {
    if (Program.ru_lang)
    {
     Console.WriteLine("Получено системное исключение, нарушившее работу следилки: " + ex.Message);
    }
    else
    {
     Console.WriteLine("System exception was received that interrupted the work of the watcher: " + ex.Message);
    }

    try
    {
     main_cycle_enabled = false;

     waitAfterKill = false;

     KillMiner();

     evt_main.Set();
    }
    catch { }
   }
  }

  private static void Pr1_DataReceived(object sender, DataReceivedEventArgs e)
  {
   //Console.WriteLine("DEBUG :: " + e.Data);

   if (e.Data.Contains("Invalid wallet address") || e.Data.Contains("Connection Error:"))
   {
    dummy_status_ok = false;

    Process p = (sender as Process);
    if (p != null && p.HasExited == false)
    {
     p.OutputDataReceived -= Pr1_DataReceived;
     p.ErrorDataReceived -= Pr1_DataReceived;

     p.Kill();
    }

    //Console.WriteLine("DEBUG :: dummy_status_ok: false");
   }
   else if (e.Data.Contains("Authorized on Stratum Server"))
   {
    dummy_status_ok = true;

    Process p = (sender as Process);
    if (p != null && p.HasExited == false)
    {
     p.OutputDataReceived -= Pr1_DataReceived;
     p.ErrorDataReceived -= Pr1_DataReceived;

     p.Kill();
    }

    //Console.WriteLine("DEBUG :: dummy_status_ok: true");
   }
  }
 }
}
