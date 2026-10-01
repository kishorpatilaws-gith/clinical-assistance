using System.Threading.Channels;

namespace ClinicalAI.Api.Application.Services
{
    public interface IDocumentIndexingQueue
    {
        void Enqueue(Guid documentId);

        IAsyncEnumerable<Guid> DequeueAllAsync(CancellationToken ct);
    }

    /// <summary>
    /// In-memory hand-off from the upload request to the indexing worker. Nothing is
    /// lost when the process restarts: the queue only carries ids, and the worker
    /// re-queues every row still Pending or Processing when it starts.
    /// </summary>
    public class DocumentIndexingQueue : IDocumentIndexingQueue
    {
        private readonly Channel<Guid> _channel =
            Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = true });

        public void Enqueue(Guid documentId) => _channel.Writer.TryWrite(documentId);

        public IAsyncEnumerable<Guid> DequeueAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
    }
}
