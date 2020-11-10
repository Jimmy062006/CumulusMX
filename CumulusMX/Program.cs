﻿using System;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.ServiceProcess;
using System.IO;

namespace CumulusMX
{
    internal class Program
    {
        public static Cumulus cumulus;
        public static bool exitSystem = false;
        public static bool service = false;
        public static TextWriterTraceListener svcTextListener;
        const string AppGuid = "57190d2e-7e45-4efb-8c09-06a176cef3f3";
        public static Mutex appMutex;
        public static DateTime StartTime;

        public static int httpport = 8998;
        public static bool debug = false;

        private static void Main(string[] args)
        {
            StartTime = DateTime.Now;
            var windows = Type.GetType("Mono.Runtime") == null;
            //var ci = new CultureInfo("en-GB");
            //System.Threading.Thread.CurrentThread.CurrentCulture = ci;

            if (!windows)
            {
                // Use reflection, so no attempt to load Mono dll on Windows

                var posixAsm = Assembly.Load("Mono.Posix, Version=4.0.0.0, Culture=neutral, PublicKeyToken=0738eb9f132ed756");
                var unixSignalType = posixAsm.GetType("Mono.Unix.UnixSignal");
                var unixSignalWaitAny = unixSignalType.GetMethod("WaitAny", new[] { unixSignalType.MakeArrayType() });
                var signumType = posixAsm.GetType("Mono.Unix.Native.Signum");

                var signals = Array.CreateInstance(unixSignalType, 1);
                signals.SetValue(Activator.CreateInstance(unixSignalType, signumType.GetField("SIGTERM").GetValue(null)), 0);

                Thread signalThread = new Thread(delegate ()
                {
                    while (!exitSystem)
                    {
                        // Wait for a signal to be delivered
                        unixSignalWaitAny?.Invoke(null, new object[] {signals});

                        cumulus.LogConsoleMessage("\nExiting system due to external SIGTERM signal");

                        exitSystem = true;
                    }
                });

                signalThread.Start();

                // Now we need to catch the console Ctrl-C
                Console.CancelKeyPress += (s, ev) =>
                {
                    cumulus.LogConsoleMessage("Ctrl+C pressed");
                    cumulus.LogConsoleMessage("\nCumulus terminating");
                    cumulus.Stop();
                    Trace.WriteLine("Cumulus has shutdown");
                    ev.Cancel = true;
                    exitSystem = true;
                };

            }
            else
            {
                // set the working path to the exe location
                Directory.SetCurrentDirectory(AppDomain.CurrentDomain.BaseDirectory);
            }

            AppDomain.CurrentDomain.UnhandledException += UnhandledExceptionTrapper;

#if DEBUG
            debug = true;
            //Debugger.Launch();
#endif

            for (int i = 0; i < args.Length; i++)
            {
                try
                {
                    switch (args[i])
                    {
                        case "-lang" when args.Length >= i:
                        {
                            var lang = args[++i];

                            CultureInfo.DefaultThreadCurrentCulture = new CultureInfo(lang);
                            CultureInfo.DefaultThreadCurrentUICulture = new CultureInfo(lang);
                            break;
                        }
                        case "-port" when args.Length >= i:
                            httpport = Convert.ToInt32(args[++i]);
                            break;
                        case "-debug":
                            // Switch on debug and and data logging from the start
                            debug = true;
                            break;
                        case "-wsport":
                            i++;
                            Console.WriteLine("The use of the -wsport command line parameter is deprecated");
                            break;
                        case "-install" when windows:
                        {
                            if (SelfInstaller.InstallMe())
                            {
                                Console.WriteLine("Cumulus MX is now installed to run as service");
                                Environment.Exit(0);
                            }
                            else
                            {
                                Console.WriteLine("Cumulus MX failed to install as service");
                                Environment.Exit(1);
                            }

                            break;
                        }
                        case "-install":
                            Console.WriteLine("You can only install Cumulus MX as a service in Windows");
                            Environment.Exit(1);
                            break;
                        case "-uninstall" when windows:
                        {
                            if (SelfInstaller.UninstallMe())
                            {
                                Console.WriteLine("Cumulus MX is no longer installed to run as service");
                                Environment.Exit(0);
                            }
                            else
                            {
                                Console.WriteLine("Cumulus MX failed uninstall itself as service");
                                Environment.Exit(1);
                            }

                            break;
                        }
                        case "-uninstall":
                            Console.WriteLine("You can only uninstall Cumulus MX as a service in Windows");
                            Environment.Exit(1);
                            break;
                        case "-service":
                            service = true;
                            break;
                        default:
                            Console.WriteLine($"Invalid command line argument \"{args[i]}\"");
                            Usage();
                            break;
                    }
                }
                catch
                {
                    Usage();
                }
            }

            using (appMutex = new Mutex(false, "Global\\" + AppGuid))
            {
                // Interactive seems to be always false under mono :(
                // So we need the no service flag & mono
                if (Environment.UserInteractive || (!service && !windows))
                {
                    service = false;
                    RunAsAConsole(httpport, debug);
                }
                else
                {
                    var logfile = "MXdiags" + Path.DirectorySeparatorChar + "ServiceConsoleLog.txt";
                    svcTextListener = new TextWriterTraceListener(logfile);
                    service = true;
                    if (File.Exists(logfile))
                    {
                        File.Delete(logfile);
                    }
                    svcTextListener = new TextWriterTraceListener(logfile);
                    ServiceBase.Run(new CumulusService());
                }

                while (!exitSystem)
                {
                    Thread.Sleep(500);
                }

                Environment.Exit(0);
            }
        }

        private static void Usage()
        {
            Console.WriteLine();
            Console.WriteLine("Valid arugments are:");
            Console.WriteLine(" -port <http_portnum> - Sets the HTTP port Cumulus will use (default 8998)");
            Console.WriteLine(" -lang <culture_name> - Sets the Language Cumulus will use (defaults to current user language)");
            Console.WriteLine(" -debug               - Switches on debug and data logging from Cumulus start");
            Console.WriteLine(" -install             - Installs Cumulus as a system service (Windows only)");
            Console.WriteLine(" -uninstall           - Removes Cumulus as a system service (Windows only)");
            Console.WriteLine(" -service             - Must be used when running as a mono-service (Linux only)");
            Console.WriteLine("\nCumulus terminating");
            Environment.Exit(1);
        }

        private static void RunAsAConsole(int port, bool debug)
        {
            //Console.WriteLine("Current culture: " + CultureInfo.CurrentCulture.DisplayName);
            if (Type.GetType("Mono.Runtime") == null)
            {
                _ = new ExitHandler();
            }

            cumulus = new Cumulus(port, debug, "");

            Console.WriteLine(DateTime.Now.ToString("G"));

            Console.WriteLine("Type Ctrl-C to terminate");
        }

        private static void UnhandledExceptionTrapper(object sender, UnhandledExceptionEventArgs e)
        {
            try
            {
                Trace.WriteLine("!!! Unhandled Exception !!!");
                Trace.WriteLine(e.ExceptionObject.ToString());

                if (service)
                {
                    svcTextListener.WriteLine(e.ExceptionObject.ToString());
                    svcTextListener.WriteLine("**** An error has occurred - please zip up the MXdiags folder and post it in the forum ****");
                    svcTextListener.Flush();
                }
                else
                {
                    Console.WriteLine(e.ExceptionObject.ToString());
                    Console.WriteLine("**** An error has occurred - please zip up the MXdiags folder and post it in the forum ****");
                    Console.WriteLine("Press Enter to terminate");
                    Console.ReadLine();
                }
                Thread.Sleep(1000);
                Environment.Exit(1);
            }
            catch (Exception)
            {
            }
        }
    }

    public class ExitHandler
    {
        [DllImport("Kernel32")]
        private static extern bool SetConsoleCtrlHandler(EventHandler handler, bool add);

        //private Program program;

        private delegate bool EventHandler(CtrlType sig);

        private static EventHandler handler;

        private enum CtrlType
        {
            CTRL_C_EVENT = 0,
            CTRL_BREAK_EVENT = 1,
            CTRL_CLOSE_EVENT = 2,
            CTRL_LOGOFF_EVENT = 5,
            CTRL_SHUTDOWN_EVENT = 6
        }

        public ExitHandler()
        {
            handler += Handler;
            SetConsoleCtrlHandler(handler, true);
        }

        private static bool Handler(CtrlType sig)
        {
            var reason = new[] { "Ctrl-C", "Ctrl-Break", "Close Main Window", "unknown", "unknown", "User Logoff", "System Shutdown" };
            //Console.WriteLine("Cumulus terminating");
            Program.cumulus.LogConsoleMessage("Cumulus terminating");

            Trace.WriteLine("Exiting system due to external: " + reason[(int)sig]);

            Program.cumulus.Stop();

            Trace.WriteLine("Cumulus has shutdown");
            Console.WriteLine("Cumulus stopped");

            //allow main to run off
            Thread.Sleep(200);
            Program.exitSystem = true;

            return true;
        }
    }
}
