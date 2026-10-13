namespace VibeCode.Services;

/// <summary>A cancelled caller can stop waiting, but the native model stays exclusively leased until its work ends.</summary>
internal sealed class NativeSpeechWorkQueue
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<T> RunAsync<T>(Func<Task<T>> work, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await work().ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }
}
