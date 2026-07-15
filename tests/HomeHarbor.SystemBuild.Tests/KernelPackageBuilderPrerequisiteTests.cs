using HomeHarbor.Tooling;

namespace HomeHarbor.SystemBuild.Tests;

[TestClass]
public sealed class KernelPackageBuilderPrerequisiteTests
{
    private const string PrimaryFingerprint = "0123456789ABCDEF0123456789ABCDEF01234567";
    private const string OtherFingerprint = "89ABCDEF0123456789ABCDEF0123456789ABCDEF";
    private const string SubkeyFingerprint = "FEDCBA9876543210FEDCBA9876543210FEDCBA98";

    [TestMethod]
    public void ParseArchPkgbuildPrerequisites_Parses_Full_Keys_And_Current_Architecture_Dependencies()
    {
        var metadata = """
            pkgbase = linux-lts
                makedepends = bc>=1.07
                makedepends_x86_64 = pahole
                makedepends_aarch64 = uboot-tools
                validpgpkeys = 0123456789abcdef0123456789abcdef01234567
                validpgpkeys = 0123456789ABCDEF0123456789ABCDEF01234567
            pkgname = linux-lts
                depends = coreutils
            """;

        var parsed = KernelPackageBuilder.ParseArchPkgbuildPrerequisites(metadata, "x86_64");

        CollectionAssert.AreEqual(new[] { PrimaryFingerprint }, parsed.ValidPgpKeys.ToArray());
        CollectionAssert.AreEqual(new[] { "bc>=1.07", "pahole" }, parsed.MakeDependencies.ToArray());
    }

    [TestMethod]
    public void ParseArchPkgbuildPrerequisites_Rejects_Short_Key_Ids()
    {
        var error = Assert.ThrowsExactly<InvalidOperationException>(() =>
            KernelPackageBuilder.ParseArchPkgbuildPrerequisites(
                "pkgbase = linux-lts\n\tvalidpgpkeys = 0123456789ABCDEF\n",
                "x86_64"));

        StringAssert.Contains(error.Message, "full 40- or 64-hex");
    }

    [TestMethod]
    public void ParsePrimaryOpenPgpFingerprints_Ignores_Subkey_Fingerprints()
    {
        var colonOutput =
            "pub:-:4096:1:0000000000000000:0:0::::::scESC::::::23::0:\n" +
            "fpr:::::::::" + PrimaryFingerprint + ":\n" +
            "sub:-:4096:1:1111111111111111:0:0::::::e::::::23:\n" +
            "fpr:::::::::" + SubkeyFingerprint + ":\n";

        var parsed = KernelPackageBuilder.ParsePrimaryOpenPgpFingerprints(colonOutput);

        CollectionAssert.AreEqual(new[] { PrimaryFingerprint }, parsed.ToArray());
        KernelPackageBuilder.RequireExactPrimaryOpenPgpFingerprints(
            [PrimaryFingerprint],
            colonOutput);
    }

    [TestMethod]
    public void RequireExactPrimaryOpenPgpFingerprints_Rejects_Unexpected_Primary_Key()
    {
        var colonOutput =
            "pub:-:4096:1:0000000000000000:0:0::::::scESC::::::23::0:\n" +
            "fpr:::::::::" + PrimaryFingerprint + ":\n" +
            "pub:-:4096:1:2222222222222222:0:0::::::scESC::::::23::0:\n" +
            "fpr:::::::::" + OtherFingerprint + ":\n";

        var error = Assert.ThrowsExactly<InvalidOperationException>(() =>
            KernelPackageBuilder.RequireExactPrimaryOpenPgpFingerprints(
                [PrimaryFingerprint],
                colonOutput));

        StringAssert.Contains(error.Message, "exactly the requested");
        StringAssert.Contains(error.Message, OtherFingerprint);
    }

    [TestMethod]
    public void RequireSatisfiedMakeDependencies_Reports_Missing_Dependencies_Before_Nodeps()
    {
        var result = new CommandResult(
            127,
            "pahole\nbc>=1.07\n",
            string.Empty,
            "pacman -T -- bc pahole");

        var error = Assert.ThrowsExactly<InvalidOperationException>(() =>
            KernelPackageBuilder.RequireSatisfiedMakeDependencies(
                ["bc>=1.07", "pahole"],
                result));

        StringAssert.Contains(error.Message, "bc>=1.07, pahole");
        StringAssert.Contains(error.Message, "makepkg --nodeps is intentionally gated");
    }

    [TestMethod]
    public void RequireSatisfiedMakeDependencies_Accepts_Success()
    {
        KernelPackageBuilder.RequireSatisfiedMakeDependencies(
            ["bc", "pahole"],
            new CommandResult(0, string.Empty, string.Empty, "pacman -T -- bc pahole"));
    }
}
