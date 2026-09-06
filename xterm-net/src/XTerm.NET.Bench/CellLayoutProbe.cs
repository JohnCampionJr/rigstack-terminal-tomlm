using System.Diagnostics;
using System.Runtime.CompilerServices;
using XTerm.Buffer;

#pragma warning disable CS0649   // fields exist to reproduce the struct layout, not to be used

namespace XTerm.Bench;

/// <summary>
/// Sizes the BufferCell.Content removal before doing it.
///
/// The claim under test: because BufferCell holds a string, an array of them is an array of GC
/// references, so every fill and every cell write emits a write barrier and the collector must trace
/// the whole scrollback. Removing the field would make the struct blittable. That is a large,
/// breaking refactor, so it is worth knowing what it actually buys.
///
/// Mirrors of the struct with and without the reference, filled and written the same way.
/// </summary>
public static class CellLayoutProbe
{
    private struct WithRef
    {
        public string Content;
        public int Width;
        public AttributeData Attributes;   // present for layout parity; never read
        public int CodePoint;
    }

    private struct WithoutRef
    {
        public int ClusterId;
        public int Width;
        public AttributeData Attributes;   // present for layout parity; never read
        public int CodePoint;
    }

    public static void Run()
    {
        const int cols = 240;
        const int iterations = 400_000;

        Console.WriteLine($"sizeof BufferCell (current) : {Unsafe.SizeOf<BufferCell>()} bytes");

        // The property that decides whether the buffer costs write barriers. Size is beside the
        // point: one managed reference anywhere in the struct makes the whole array traced.
        Console.WriteLine($"BufferCell contains refs    : "
            + $"{System.Runtime.CompilerServices.RuntimeHelpers.IsReferenceOrContainsReferences<BufferCell>()}"
            + "   <- false is the whole point");
        Console.WriteLine($"sizeof with    reference    : {Unsafe.SizeOf<WithRef>()} bytes");
        Console.WriteLine($"sizeof without reference    : {Unsafe.SizeOf<WithoutRef>()} bytes");
        Console.WriteLine();

        var a = new WithRef[cols];
        var b = new WithoutRef[cols];
        var fillA = new WithRef { Content = " ", Width = 1, CodePoint = 32 };
        var fillB = new WithoutRef { ClusterId = 0, Width = 1, CodePoint = 32 };

        for (var w = 0; w < 5_000; w++) { Array.Fill(a, fillA); Array.Fill(b, fillB); }

        var sw = Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++) Array.Fill(a, fillA);
        sw.Stop();
        var withRefFill = sw.Elapsed.TotalMilliseconds * 1_000_000.0 / iterations;

        sw.Restart();
        for (var i = 0; i < iterations; i++) Array.Fill(b, fillB);
        sw.Stop();
        var withoutRefFill = sw.Elapsed.TotalMilliseconds * 1_000_000.0 / iterations;

        Console.WriteLine($"Array.Fill of {cols} cells");
        Console.WriteLine($"  with    reference : {withRefFill,8:N1} ns   ({withRefFill / cols:N2} ns/cell)");
        Console.WriteLine($"  without reference : {withoutRefFill,8:N1} ns   ({withoutRefFill / cols:N2} ns/cell)");
        Console.WriteLine($"  speedup           : {withRefFill / withoutRefFill,8:N2}x");

        // Single-cell writes, which is what printing does.
        sw.Restart();
        for (var i = 0; i < iterations; i++)
            for (var x = 0; x < cols; x++) a[x] = fillA;
        sw.Stop();
        var withRefWrite = sw.Elapsed.TotalMilliseconds * 1_000_000.0 / iterations / cols;

        sw.Restart();
        for (var i = 0; i < iterations; i++)
            for (var x = 0; x < cols; x++) b[x] = fillB;
        sw.Stop();
        var withoutRefWrite = sw.Elapsed.TotalMilliseconds * 1_000_000.0 / iterations / cols;

        Console.WriteLine();
        Console.WriteLine($"per-cell assignment");
        Console.WriteLine($"  with    reference : {withRefWrite,8:N2} ns/cell");
        Console.WriteLine($"  without reference : {withoutRefWrite,8:N2} ns/cell");
        Console.WriteLine($"  speedup           : {withRefWrite / withoutRefWrite,8:N2}x");
    }
}
