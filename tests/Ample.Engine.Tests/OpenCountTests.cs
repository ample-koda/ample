using System.Reflection;
using Ample.Core.Abstractions;
using Ample.Engine.Engine;
using Ample.Audio.IO;
using Ample.Storage.Projects;
using Ample.Storage.Takes;

namespace Ample.Core.Tests;

/// <summary>A file system that counts the data it hands out, to see what a build really reads.</summary>
public class CountingFileSystem : DispatchProxy
{
    /// <summary>The real file system the calls go to.</summary>
    public IFileSystem Inner { get; set; } = null!;

    /// <summary>How many files were opened for reading.</summary>
    public int Opens { get; set; }

    /// <summary>How many files were read whole.</summary>
    public int Reads { get; set; }

    /// <inheritdoc />
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod!.Name == "OpenRead")
        {
            Opens++;
        }

        if (targetMethod.Name == "ReadAllBytesAsync")
        {
            Reads++;
        }

        try
        {
            return targetMethod.Invoke(Inner, args);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }
}

/// <summary>What a build that finds everything in the cache reads: nothing, because the audio of the blocks before it is only needed to render.</summary>
public sealed class OpenCountTests
{
    [Fact]
    public async Task A_build_that_finds_every_block_in_the_cache_opens_no_audio_data()
    {
        string folder = Directory.CreateTempSubdirectory("ample-opencount-").FullName;
        try
        {
            string root = Path.Combine(folder, "proj");
            IFileSystem fs = DispatchProxy.Create<IFileSystem, CountingFileSystem>();
            CountingFileSystem counter = (CountingFileSystem)(object)fs;
            counter.Inner = new PhysicalFileSystem();
            using OpenProject project = await ProjectStore.CreateAsync(fs, new SystemClock(), new GuidV7Generator(), root, "engine");
            Takebank bank = Takebank.Open(fs, new SystemClock(), root);
            BuildEngine engine = new(bank, EngineRig.Spec, project.Manifest.Id, root);

            BuildReport cold = await engine.BuildAsync(EngineRig.Parse(EngineRig.Hello));
            Assert.All(cold.Blocks, block => Assert.Equal("rendered", block.Status));
            Assert.True(counter.Opens > 0, "a cold build reads the audio of the blocks before a block");   // the counter really counts
            counter.Opens = 0;
            counter.Reads = 0;

            BuildReport warm = await engine.BuildAsync(EngineRig.Parse(EngineRig.Hello));
            Assert.All(warm.Blocks, block => Assert.Equal("cached", block.Status));
            Assert.Equal(cold.MasterTakeId, warm.MasterTakeId);   // the plan ids did not change: the same takes are found
            Assert.Equal(0, counter.Opens);
            Assert.Equal(0, counter.Reads);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
}
