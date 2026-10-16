using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Elekta.IC.Tools.SoupAnalysis.Desktop.ViewModels;

namespace Elekta.IC.Tools.SoupAnalysis.Desktop.Presentation;

public sealed class BoundedLog : ObservableCollection<LogEntry>
{
    public void AppendBatch(IEnumerable<LogEntry> batch)
    {
        foreach (var entry in batch) Items.Add(entry);
        while (Items.Count > 500) Items.RemoveAt(0);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}