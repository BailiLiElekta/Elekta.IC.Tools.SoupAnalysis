namespace Elekta.IC.Tools.SoupAnalysis.Infrastructure;

internal static class FileReplacement
{
    public static async Task ReplaceAsync(string temporary, string destination, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { File.Move(temporary, destination, overwrite: true); return; }
            catch (Exception exception) when (attempt < 4 && exception is IOException or UnauthorizedAccessException
                && (exception.HResult & 0xffff) is 5 or 32 or 33)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100 * (1 << attempt)), cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
