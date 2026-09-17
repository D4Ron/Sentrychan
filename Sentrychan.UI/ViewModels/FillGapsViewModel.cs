using ReactiveUI;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;

namespace Sentrychan.UI.ViewModels;

public class FillGapsViewModel : ViewModelBase
{
    public ObservableCollection<FillGapRowVm> Rows { get; }

    public ReactiveCommand<Unit, System.Collections.Generic.List<Sentrychan.Core.Interfaces.FillGapResult>> ConfirmCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }

    public FillGapsViewModel(System.Collections.Generic.IEnumerable<Sentrychan.Core.Interfaces.FillGapResult> results)
    {
        Rows = new ObservableCollection<FillGapRowVm>(results.Select(r => new FillGapRowVm(r)));

        ConfirmCommand = ReactiveCommand.Create(() => 
        {
            return Rows.Select(r => r.GetResult()).ToList();
        });

        CancelCommand = ReactiveCommand.Create(() => { });
    }
}
