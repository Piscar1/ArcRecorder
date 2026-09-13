using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ArcRecorder
{
    /// <summary>
    /// Job Object с KILL_ON_JOB_CLOSE: дочерние ffmpeg умирают вместе с ArcRecorder, даже если его убили или он упал.
    /// Без этого ffmpeg-сирота бесконечно захватывал экран и писал сегменты в %TEMP%
    /// (раньше его останавливал конец звука через -shortest, после отказа от -shortest — уже нет).
    /// </summary>
    static class ChildProcessJob
    {
        const int JobObjectExtendedLimitInformation = 9;
        const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr CreateJobObject(IntPtr attributes, string name);
        [DllImport("kernel32.dll")]
        static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, int length);
        [DllImport("kernel32.dll")]
        static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [StructLayout(LayoutKind.Sequential)]
        struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct IO_COUNTERS
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
            public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }

        // Хэндл держим до конца жизни процесса: Windows закроет его при выходе/падении — и убьёт всех в джобе
        static readonly IntPtr Job = Create();

        static IntPtr Create()
        {
            try
            {
                var job = CreateJobObject(IntPtr.Zero, null);
                if (job == IntPtr.Zero) return IntPtr.Zero;
                var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
                if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref info,
                        Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
                {
                    AppLog.Write("error.log", "Job Object: SetInformationJobObject не удался, код " + Marshal.GetLastWin32Error());
                }
                return job;
            }
            catch (Exception ex)
            {
                AppLog.Error("ChildProcessJob", ex);
                return IntPtr.Zero;
            }
        }

        /// <summary>Привязать дочерний процесс к джобе: он не переживёт ArcRecorder.</summary>
        public static void Add(Process process)
        {
            if (Job == IntPtr.Zero || process == null) return;
            try
            {
                if (!AssignProcessToJobObject(Job, process.Handle))
                    AppLog.Write("error.log", "Job Object: AssignProcessToJobObject не удался, код " + Marshal.GetLastWin32Error());
            }
            catch (Exception ex) { AppLog.Error("ChildProcessJob.Add", ex); }
        }
    }
}
