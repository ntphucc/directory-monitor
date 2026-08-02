using System.Collections.Specialized;
using DirectoryMonitor.Presentation.Collections;

namespace DirectoryMonitor.Tests;

public sealed class BulkObservableCollectionTests
{
    [Fact]
    public void Append_batches_keep_fifo_order_and_trim_the_oldest_items_once()
    {
        var collection = new BulkObservableCollection<int>();
        var notifications = 0;
        collection.CollectionChanged += (_, args) =>
        {
            notifications++;
            Assert.Equal(NotifyCollectionChangedAction.Reset, args.Action);
        };

        Assert.Empty(collection.AppendRangeAndTrimStart([1, 2, 3], 5));
        var removed = collection.AppendRangeAndTrimStart([4, 5, 6], 5);

        Assert.Equal([1], removed);
        Assert.Equal([2, 3, 4, 5, 6], collection);
        Assert.Equal(2, notifications);
    }

    [Fact]
    public void Lowering_the_buffer_limit_trims_from_the_start_without_new_items()
    {
        var collection = new BulkObservableCollection<int>();
        collection.AppendRangeAndTrimStart([1, 2, 3, 4], 4);

        var removed = collection.AppendRangeAndTrimStart([], 2);

        Assert.Equal([1, 2], removed);
        Assert.Equal([3, 4], collection);
    }
}
