using Nadlan.Core.Validation;
using Nadlan.Host.Files;

namespace Nadlan.Core.Tests;

/// <summary>
/// The slices of a stored file that File details reads (N3). Review 2026-11-01: a start at or past the file's end made the
/// count negative, S3 ignored the bad range and sent the whole object - a 1.1 GB drone video into a 1 GB server.
/// </summary>
public class FileDetailsTests
{
    private const long Size = 3_088_952;

    [Theory]
    [InlineData(4_000_000, 4_100_000)] // the reviewer's case: wholly past the end
    [InlineData(Size, Size + 16)]      // exactly at the end
    public void A_start_at_or_past_the_end_reads_nothing(long start, long end)
        => Assert.Null(FileDetailsEndpoints.Slice(start, end, Size));

    [Fact]
    public void An_end_past_the_end_is_cut_at_the_end()
        => Assert.Equal((Size - 100, 100), FileDetailsEndpoints.Slice(Size - 100, Size + 5_000, Size));

    [Fact]
    public void A_slice_inside_the_file_is_read_as_asked()
        => Assert.Equal((0L, 262_144), FileDetailsEndpoints.Slice(0, 262_144, Size));

    [Theory]
    [InlineData(-1, 10)]             // before the start
    [InlineData(10, 10)]             // empty
    [InlineData(20, 10)]             // backwards
    [InlineData(0, 8 * 1048576 + 1)] // over 8 MB at a time
    public void Bad_or_too_large_requests_are_refused(long start, long end)
        => Assert.Equal("FILE_RANGE_INVALID", Assert.Throws<DomainValidationException>(() => FileDetailsEndpoints.Slice(start, end, 2_000_000_000)).Code);
}
