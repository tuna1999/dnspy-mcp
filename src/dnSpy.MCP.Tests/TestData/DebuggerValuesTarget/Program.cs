using System;
using System.Collections;
using System.Collections.Generic;
using System.Dynamic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;

public static class ValuesTarget {
    public static int SideEffects;
    static bool workers;
    static int ready;

    public sealed class DormantType {
        static DormantType() { Interlocked.Increment(ref SideEffects); }
        public static int Value = 9;
    }

    public sealed class PayloadProxy {
        public PayloadProxy(Payload value) { Interlocked.Increment(ref SideEffects); }
    }

    public sealed class DynamicPayload : DynamicObject {
        public override IEnumerable<string> GetDynamicMemberNames() {
            Interlocked.Increment(ref SideEffects);
            return new[] { "Dangerous" };
        }
        public override bool TryGetMember(GetMemberBinder binder, out object result) {
            Interlocked.Increment(ref SideEffects);
            result = -1;
            return true;
        }
    }

    [DebuggerDisplay("{Dangerous}")]
    [DebuggerTypeProxy(typeof(PayloadProxy))]
    public sealed class Payload : IEnumerable {
        public int Number = 42;
        public string Text = "fixture-value";
        public int[] Numbers = { 3, 5, 8 };
        public Payload Next;
        public DormantType Dormant;
        public DynamicPayload Dynamic = new DynamicPayload();
        public int[] LargeNumbers = new int[4200];
        public int Dangerous {
            get { Interlocked.Increment(ref SideEffects); return -1; }
        }
        public override string ToString() {
            Interlocked.Increment(ref SideEffects); return "must-not-call";
        }
        public IEnumerator GetEnumerator() {
            Interlocked.Increment(ref SideEffects); return Numbers.GetEnumerator();
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        public int Inspect(int input) {
            int total = input + Number;
            string decoded = "decoded-" + input;
            int[] values = Numbers;
            Checkpoint(this, input, total, decoded, values);
            GC.KeepAlive(decoded);
            GC.KeepAlive(values);
            return total;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Checkpoint(Payload payload, int argument,
        int total, string decoded, int[] values) {
        if (workers) {
            if (Interlocked.Increment(ref ready) == 2) Console.WriteLine("ready:2");
            Thread.Sleep(Timeout.Infinite);
        }
        GC.KeepAlive(payload);
        GC.KeepAlive(argument);
        GC.KeepAlive(total);
        GC.KeepAlive(decoded);
        GC.KeepAlive(values);
    }

    public static void Main(string[] args) {
        if (args.Length != 0 && args[0] == "--wait-for-debugger") {
            Console.WriteLine("ready:attach");
            while (!Debugger.IsAttached) Thread.Sleep(20);
        }
        workers = args.Length != 0 && args[0] == "--workers";
        if (workers) {
            new Thread(() => new Payload().Inspect(11)).Start();
            new Thread(() => new Payload().Inspect(13)).Start();
            Thread.Sleep(Timeout.Infinite);
        }
        var value = new Payload();
        value.Next = value;
        int result = value.Inspect(7);
        Console.WriteLine("result=" + result + ";side_effects=" + SideEffects);
    }
}
