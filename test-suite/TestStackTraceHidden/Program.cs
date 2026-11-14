using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using DbgTest;
using DbgTest.DAP;
using DbgTest.Script;
using Newtonsoft.Json;

namespace TestStackTraceHidden
{
class Program
{
    static ExceptionDispatchInfo? _captured;

    static void Main(string[] args)
    {
        Label.Checkpoint("init", "", (object context) =>
        {
            Context Context = (Context)context;
            foreach (bool jmc in new[] { true, false })
                Context.CheckHiddenFrames(@"__FILE__:__LINE__", jmc);
            Context.CheckHiddenForeignFrames(@"__FILE__:__LINE__");
            Context.DebuggerExit(@"__FILE__:__LINE__");
        });

        if (args.Length != 0)
        {
            var worker = new System.Threading.Thread(Capture);
            worker.Start();
            worker.Join();
            AppDomain.CurrentDomain.UnhandledException += OnUnhandled;
            _captured!.Throw();
            return;
        }
        int marker = 70;
        Outer();                                                     Label.Breakpoint("mainCall");
        GC.KeepAlive(marker);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Outer()
    {
        int marker = 60;
        HiddenType.Nested.Call();                                     Label.Breakpoint("outerCall");
        HiddenStep();                                                Label.Breakpoint("stepCall");
        GC.KeepAlive(marker);                                        Label.Breakpoint("afterStep");
        HiddenBreakpoint();                                          Label.Breakpoint("hiddenCall");
        GC.KeepAlive(marker);
    }

    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void HiddenMethod()
    {
        int marker = 50;
        HiddenType.Call();
        GC.KeepAlive(marker);
    }

    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void HiddenStep()
    {
        GC.KeepAlive(80);                                             Label.Breakpoint("stepBody");
    }

    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void HiddenBreakpoint()
    {
        GC.KeepAlive(90);                                             Label.Breakpoint("hiddenBody");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Leaf()
    {
        int marker = 10;
        GC.KeepAlive(marker);                                         Label.Breakpoint("log");
        GC.KeepAlive(marker);                                         Label.Breakpoint("leaf");
    }

    static void Capture()
    {
        try
        {
            HiddenType.ThrowOrigin();                                Label.Breakpoint("capture");
        }
        catch (Exception error) { _captured = ExceptionDispatchInfo.Capture(error); }
    }

    static void OnUnhandled(object sender, UnhandledExceptionEventArgs args)
    {
        GC.KeepAlive(args);                                           Label.Breakpoint("foreignLog");
        GC.KeepAlive(args);                                           Label.Breakpoint("foreignStop");
    }
}

[StackTraceHidden]
class HiddenType
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Call()
    {
        int marker = 40;
        Program.Leaf();
        GC.KeepAlive(marker);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ThrowOrigin()
    {
        throw new InvalidOperationException("hidden foreign origin");
    }

    public class Nested
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Call()
        {
            int marker = 55;
            new Derived().Call();                                    Label.Breakpoint("nestedCall");
            GC.KeepAlive(marker);
        }
    }
}

class Derived : HiddenType
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public new void Call()
    {
        int marker = 30;
        Lookalike();                                                  Label.Breakpoint("derivedCall");
        GC.KeepAlive(marker);
    }

    [Decoy.StackTraceHidden]
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Lookalike()
    {
        int marker = 20;
        System.Runtime.CompilerServices.AsyncTaskMethodBuilderLookalike.Call(); Label.Breakpoint("lookalikeCall");
        GC.KeepAlive(marker);
    }
}
}

namespace Decoy
{
[AttributeUsage(AttributeTargets.Method)]
sealed class StackTraceHiddenAttribute : Attribute { }
}

namespace System.Runtime.CompilerServices
{
class AsyncTaskMethodBuilderLookalike
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Call()
    {
        int marker = 15;
        TestStackTraceHidden.Program.HiddenMethod();                  Label.Breakpoint("prefixCall");
        GC.KeepAlive(marker);
    }
}
}

namespace DbgTest.Script
{
partial class Context
{
    const string HiddenPrefix = "TestStackTraceHidden.";
    static readonly string[] HiddenVisibleNames =
    {
        HiddenPrefix + "Program.Leaf()",
        "System.Runtime.CompilerServices.AsyncTaskMethodBuilderLookalike.Call()",
        HiddenPrefix + "Derived.Lookalike()",
        HiddenPrefix + "Derived.Call()",
        HiddenPrefix + "HiddenType.Nested.Call()",
        HiddenPrefix + "Program.Outer()",
        HiddenPrefix + "Program.Main(string[] args)",
    };

    public void CheckHiddenFrames(string trace, bool jmc)
    {
        Initialize(trace);
        AddBreakpoint(trace, "log", logMessage: "hidden={$CALLSTACK}|caller={$CALLER}");
        foreach (string label in new[] { "leaf", "stepCall", "afterStep", "hiddenBody" })
            AddBreakpoint(trace, label);
        SetBreakpoints(trace);
        ConfigurationDone(trace);
        Launch(trace, jmc: jmc);
        WasEntryPointHit(trace);
        Continue(trace);
        WasBreakpointHit(trace, "leaf");
        WasOutputEvent(trace, "console", "hidden=" + JsonConvert.SerializeObject(string.Join("\n", HiddenVisibleNames))
            + "|caller=" + JsonConvert.SerializeObject(HiddenPrefix + "HiddenType.Call()") + "\n");
        TestStackTrace(trace, HiddenVisibleNames,
            new[] { "leaf", "prefixCall", "lookalikeCall", "derivedCall", "nestedCall", "outerCall", "mainCall" });
        StackTraceResponse full = HiddenStack(trace, 0, 100);
        Assert.Equal(7, full.body.totalFrames, trace);
        Assert.Equal(7, full.body.stackFrames.Count, trace);
        int[] markers = { 10, 15, 20, 30, 55, 60, 70 };
        for (int i = 0; i < markers.Length; i++)
        {
            DbgTest.DAP.StackFrame frame = full.body.stackFrames[i];
            Assert.Equal(HiddenVisibleNames[i], frame.name, trace);
            GetAndCheckValue(trace, frame.id, markers[i].ToString(), "int", "marker");
            VariablesResponse locals = GetLocalVariables(trace, frame.id);
            CheckEnum(trace, locals, "marker", markers[i].ToString());
            for (int j = 0; j < i; j++)
                Assert.NotEqual(full.body.stackFrames[j].id, frame.id, trace);
        }
        foreach (var page in new[] { (0, 1), (1, 2), (3, 3), (6, 1), (7, 2), (0, 0) })
        {
            StackTraceResponse sliced = HiddenStack(trace, page.Item1, page.Item2);
            int count = page.Item2 == 0 ? 7 : Math.Min(page.Item2, 7 - page.Item1);
            Assert.Equal(count, sliced.body.stackFrames.Count, trace);
            for (int i = 0; i < count; i++)
            {
                Assert.Equal(HiddenVisibleNames[page.Item1 + i], sliced.body.stackFrames[i].name, trace);
                GetAndCheckValue(trace, sliced.body.stackFrames[i].id, markers[page.Item1 + i].ToString(), "int", "marker");
            }
        }
        Continue(trace);
        WasBreakpointHit(trace, "stepCall");
        StepIn(trace);
        HiddenStop(trace, "step");
        TestStackTrace(trace, new[] { HiddenVisibleNames[5], HiddenVisibleNames[6] }, new[] { "stepCall", "mainCall" });
        StepOut(trace);
        HiddenStop(trace, "step");
        TestStackTrace(trace, new[] { HiddenVisibleNames[5], HiddenVisibleNames[6] }, new[] { "stepCall", "mainCall" });
        Continue(trace);
        WasBreakpointHit(trace, "afterStep");
        Continue(trace);
        HiddenStop(trace, "breakpoint");
        TestStackTrace(trace, new[] { HiddenVisibleNames[5], HiddenVisibleNames[6] }, new[] { "hiddenCall", "mainCall" });
        StackTraceResponse hiddenTop = HiddenStack(trace, 0, 100);
        Assert.Equal(2, hiddenTop.body.totalFrames, trace);
        GetAndCheckValue(trace, hiddenTop.body.stackFrames[1].id, "70", "int", "marker");
        Continue(trace);
        WasExit(trace);
    }

    StackTraceResponse HiddenStack(string trace, int start, int levels)
    {
        var request = new StackTraceRequest();
        request.arguments.threadId = m_threadId;
        request.arguments.startFrame = start;
        request.arguments.levels = levels;
        var result = m_dapDebugger.Request(request);
        Assert.True(result.Success, trace);
        return JsonConvert.DeserializeObject<StackTraceResponse>(result.ResponseStr)!;
    }

    void HiddenStop(string trace, string reason)
    {
        Assert.True(m_dapDebugger.IsEventReceived(json =>
        {
            if (!m_dapDebugger.IsResponseContainProperty(json, "event", "stopped"))
                return false;
            Assert.Equal(reason, m_dapDebugger.GetResponsePropertyValue(json, "reason"), trace);
            m_threadId = Convert.ToInt32(m_dapDebugger.GetResponsePropertyValue(json, "threadId"));
            return true;
        }), trace);
    }

    public void CheckHiddenForeignFrames(string trace)
    {
        Initialize(trace);
        AddArgsListEntry("foreign");
        AddExceptionBreakpointFilterUnhandledWithConditions("!*");
        SetExceptionBreakpoints(trace);
        AddBreakpoint(trace, "foreignLog", logMessage: "foreign={$CALLSTACK}");
        AddBreakpoint(trace, "foreignStop");
        SetBreakpoints(trace);
        ConfigurationDone(trace);
        Launch(trace);
        WasEntryPointHit(trace);
        Continue(trace);
        HiddenStop(trace, "breakpoint");
        string[] names =
        {
            "[Exception] " + HiddenPrefix + "HiddenType.ThrowOrigin()",
            "[Exception] " + HiddenPrefix + "Program.Capture()",
            HiddenPrefix + "Program.OnUnhandled(object sender, System.UnhandledExceptionEventArgs args)",
            "[External Code]",
            HiddenPrefix + "Program.Main(string[] args)",
        };
        WasOutputEvent(trace, "console", "foreign=" + JsonConvert.SerializeObject(string.Join("\n", names)) + "\n");
        StackTraceResponse stack = HiddenStack(trace, 0, 100);
        Assert.Equal(names.Length, stack.body.totalFrames, trace);
        Assert.Equal(names.Length, stack.body.stackFrames.Count, trace);
        for (int i = 0; i < names.Length; i++)
            Assert.Equal(names[i], stack.body.stackFrames[i].name, trace);
        Continue(trace);
        WasExit(trace, checkExitCode: null);
    }
}
}
