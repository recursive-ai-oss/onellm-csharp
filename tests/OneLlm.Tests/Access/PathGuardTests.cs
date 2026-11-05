using OneLlm.Access;
using OneLlm.Tests.Support;
using OneLlm.Tools;

namespace OneLlm.Tests.Access;

public class PathGuardTests
{
    private static bool CanLink(Scratch s)
    {
        string probe = Path.Combine(s.Outside, "probe-link");
        bool ok = Scratch.TryLink(probe, s.Outside, directory: true);
        if (ok)
        {
            Directory.Delete(probe);
        }

        return ok;
    }

    [Fact]
    public void A_path_under_the_root_resolves_and_a_missing_write_target_is_allowed()
    {
        using var s = new Scratch();
        Capabilities caps = Capabilities.Files(s.Root);
        s.Write("a/b.txt", "x");
        Assert.Equal(Path.GetFullPath(s.In("a/b.txt")), caps.ResolveRead("a/b.txt"));
        Assert.Equal(Path.GetFullPath(s.In("new/dir/file.txt")), caps.ResolveWrite("new/dir/file.txt"));
        Assert.Equal(Path.GetFullPath(s.Root), caps.ResolveRead("."));
    }

    [Fact]
    public void An_ungranted_absolute_path_is_refused()
    {
        using var s = new Scratch();
        Capabilities caps = Capabilities.Files(s.Root);
        File.WriteAllText(Path.Combine(s.Outside, "secret.txt"), "s");
        Assert.Throws<CapabilityMissingException>(() => caps.ResolveRead(Path.Combine(s.Outside, "secret.txt")));
        Assert.Throws<CapabilityMissingException>(() => caps.ResolveWrite(Path.Combine(s.Outside, "new.txt")));
    }

    [Theory]
    [InlineData("../outside/secret.txt")]
    [InlineData("a/../../outside/secret.txt")]
    [InlineData("./a/./../../outside/secret.txt")]
    [InlineData("..")]
    public void Dot_dot_traversal_is_refused(string path)
    {
        using var s = new Scratch();
        Directory.CreateDirectory(s.In("a"));
        Capabilities caps = Capabilities.Files(s.Root);
        Assert.Throws<CapabilityMissingException>(() => caps.ResolveRead(path));
        Assert.Throws<CapabilityMissingException>(() => caps.ResolveWrite(path));
    }

    [Fact]
    public void A_sibling_with_the_same_prefix_is_not_inside_the_root()
    {
        using var s = new Scratch();
        string sibling = s.Root + "-extra";
        Directory.CreateDirectory(sibling);
        Capabilities caps = Capabilities.Files(s.Root);
        Assert.Throws<CapabilityMissingException>(() => caps.ResolveRead(sibling));
        Assert.True(PathGuard.IsUnder(s.Root + Path.DirectorySeparatorChar + "x", s.Root));
        Assert.False(PathGuard.IsUnder(sibling, s.Root));
    }

    [Fact]
    public void A_file_symlink_out_of_the_root_is_refused_for_read_and_write()
    {
        using var s = new Scratch();
        string secret = Path.Combine(s.Outside, "secret.txt");
        File.WriteAllText(secret, "s");
        if (!Scratch.TryLink(s.In("link.txt"), secret, directory: false))
        {
            return;
        }

        Capabilities caps = Capabilities.Files(s.Root);
        Assert.Throws<CapabilityMissingException>(() => caps.ResolveRead("link.txt"));
        Assert.Throws<CapabilityMissingException>(() => caps.ResolveWrite("link.txt"));
    }

    [Fact]
    public void A_directory_symlink_out_of_the_root_is_refused_including_a_new_file_beneath_it()
    {
        using var s = new Scratch();
        if (!Scratch.TryLink(s.In("dir-link"), s.Outside, directory: true))
        {
            return;
        }

        Capabilities caps = Capabilities.Files(s.Root);
        Assert.Throws<CapabilityMissingException>(() => caps.ResolveRead("dir-link"));
        Assert.Throws<CapabilityMissingException>(() => caps.ResolveRead("dir-link/secret.txt"));
        Assert.Throws<CapabilityMissingException>(() => caps.ResolveWrite("dir-link/new.txt"));
        Assert.Throws<CapabilityMissingException>(() => caps.ResolveWrite("dir-link/deep/er/new.txt"));
    }

    [Fact]
    public void A_dangling_symlink_pointing_out_of_the_root_is_refused_for_write()
    {
        using var s = new Scratch();
        string target = Path.Combine(s.Outside, "does-not-exist-yet.txt");
        if (!Scratch.TryLink(s.In("dangling"), target, directory: false))
        {
            return;
        }

        Capabilities caps = Capabilities.Files(s.Root);
        Assert.Throws<CapabilityMissingException>(() => caps.ResolveWrite("dangling"));
        Assert.Throws<CapabilityMissingException>(() => caps.ResolveRead("dangling"));
        Assert.False(File.Exists(target));
    }

    [Fact]
    public void A_dot_dot_after_a_symlink_applies_to_the_real_directory()
    {
        using var s = new Scratch();
        Directory.CreateDirectory(Path.Combine(s.Outside, "inner"));
        if (!Scratch.TryLink(s.In("hop"), Path.Combine(s.Outside, "inner"), directory: true))
        {
            return;
        }

        // hop/.. is the parent of the real directory (outside), not the root.
        Capabilities caps = Capabilities.Files(s.Root);
        Assert.Throws<CapabilityMissingException>(() => caps.ResolveRead("hop/.."));
    }

    [Fact]
    public void A_symlink_that_stays_inside_the_root_is_allowed_and_loops_are_refused()
    {
        using var s = new Scratch();
        s.Write("real/f.txt", "x");
        if (!Scratch.TryLink(s.In("alias"), s.In("real"), directory: true))
        {
            return;
        }

        Capabilities caps = Capabilities.Files(s.Root);
        Assert.Equal(Path.GetFullPath(s.In("real/f.txt")), caps.ResolveRead("alias/f.txt"));
        Assert.True(Scratch.TryLink(s.In("loop-a"), s.In("loop-b"), directory: false));
        Assert.True(Scratch.TryLink(s.In("loop-b"), s.In("loop-a"), directory: false));
        Assert.Throws<CapabilityMissingException>(() => caps.ResolveRead("loop-a"));
    }

    [Fact]
    public void A_symlink_swapped_in_between_the_check_and_the_open_is_caught()
    {
        using var s = new Scratch();
        string file = s.Write("target.txt", "inside");
        string secret = Path.Combine(s.Outside, "secret.txt");
        File.WriteAllText(secret, "outside");
        if (!CanLink(s))
        {
            return;
        }

        Capabilities caps = Capabilities.Files(s.Root);
        string real = Path.GetFullPath(file);

        // The hook runs after the first resolution and before the open, as an attacker with write access to the tree would.
        GuardedFile.BeforeOpen = p =>
        {
            if (p == real)
            {
                File.Delete(p);
                Scratch.TryLink(p, secret, directory: false);
            }
        };
        try
        {
            Assert.Throws<CapabilityMissingException>(() => GuardedFile.OpenRead(caps, "target.txt").Dispose());
        }
        finally
        {
            GuardedFile.BeforeOpen = null;
        }
    }

    [Fact]
    public void A_write_through_a_swapped_link_does_not_truncate_the_outside_file()
    {
        using var s = new Scratch();
        string file = s.Write("target.txt", "inside");
        string secret = Path.Combine(s.Outside, "secret.txt");
        File.WriteAllText(secret, "outside content");
        if (!CanLink(s))
        {
            return;
        }

        Capabilities caps = Capabilities.Files(s.Root);
        string real = Path.GetFullPath(file);
        GuardedFile.BeforeOpen = p =>
        {
            if (p == real)
            {
                File.Delete(p);
                Scratch.TryLink(p, secret, directory: false);
            }
        };
        try
        {
            Assert.Throws<CapabilityMissingException>(() => GuardedFile.OpenWrite(caps, "target.txt", append: false).Dispose());
        }
        finally
        {
            GuardedFile.BeforeOpen = null;
        }

        Assert.Equal("outside content", File.ReadAllText(secret));
    }

    [Fact]
    public void A_read_only_root_allows_reads_and_refuses_writes()
    {
        using var s = new Scratch();
        s.Write("f.txt", "x");
        Capabilities caps = Capabilities.ReadOnlyFiles(s.Root);
        Assert.NotNull(caps.ResolveRead("f.txt"));
        Assert.Throws<CapabilityMissingException>(() => caps.ResolveWrite("f.txt"));
        Assert.Empty(caps.FileRoots);
    }

    [Fact]
    public void No_file_grant_refuses_everything()
    {
        Assert.Throws<CapabilityMissingException>(() => Capabilities.None.ResolveRead("x"));
        Assert.Throws<CapabilityMissingException>(() => ((ICapabilities)NoCapabilities.Instance).ResolveRead("x"));
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("nul.txt")]
    [InlineData("COM1")]
    [InlineData("lpt9.log")]
    [InlineData("file.txt:stream")]
    [InlineData("AUX.")]
    public void Windows_device_names_and_streams_are_refused(string part)
    {
        // The rule is applied to every component on Windows; it is exercised directly here so Linux runs cover it.
        Assert.Throws<CapabilityMissingException>(() => PathGuard.CheckWindowsName(part, part));
    }

    [Theory]
    [InlineData("console.txt")]
    [InlineData("command.cs")]
    [InlineData("a.b")]
    public void Ordinary_names_are_not_mistaken_for_devices(string part) => PathGuard.CheckWindowsName(part, part);

    [Fact]
    public void Windows_and_macOS_compare_paths_ignoring_case()
    {
        Assert.True(PathGuard.IsUnder("/Work/Sub/f.txt", "/work", StringComparison.OrdinalIgnoreCase));
        Assert.False(PathGuard.IsUnder("/Work/Sub/f.txt", "/work", StringComparison.Ordinal));
        Assert.False(PathGuard.IsUnder("/Workshop/f.txt", "/work", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Relative_paths_start_from_the_working_directory()
    {
        using var s = new Scratch();
        s.Write("sub/f.txt", "x");
        Capabilities caps = Capabilities.Files(s.Root).WithWorkingDirectory("sub");
        Assert.Equal(Path.GetFullPath(s.In("sub/f.txt")), caps.ResolveRead("f.txt"));
        Assert.Throws<CapabilityMissingException>(() => Capabilities.Files(s.Root).WithWorkingDirectory(".."));
    }
}
