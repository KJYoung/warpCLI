using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
// using System.Windows.Controls;
/// using System.Windows.Data;
/// using System.Windows.Documents;
using System.Windows.Input;
/// using System.Windows.Media;
/// using System.Windows.Media.Imaging;
/// using System.Windows.Navigation;
/// using System.Windows.Shapes;
using Warp.Tools;

namespace Warp.Controls
{
    /// <summary>
    /// Interaction logic for DialogTomoReconstruction.xaml
    /// </summary>
    public partial class DialogTomoReconstruction
    {
        private int NParallel = 1;

        private TiltSeries[] Series;
        private Options Options;

        bool IsCanceled = false;

        public DialogTomoReconstruction(TiltSeries[] series, Options options)
        {
            Series = series;
            Options = options;
        }

        public async Task ButtonReconstruct_OnClick(bool isFilter, bool isManual)
        {
            Options.Tasks.TomoFullReconstructPixel = 10.0m;
            Options.Tasks.InputInvert = true;
            Options.Tasks.InputNormalize = true;
            Options.Tasks.TomoFullReconstructDoDeconv = true;
            Options.Tasks.TomoFullReconstructDeconvStrength = 1.00m;
            Options.Tasks.TomoFullReconstructDeconvFalloff = 1.00m;
            Options.Tasks.TomoFullReconstructDeconvHighpass = 300m;
            Options.Tasks.TomoFullReconstructPrepareDenoising = false;
            Options.Tasks.TomoFullReconstructOnlyFullVoxels = false;
            Options.Tasks.IncludeFilteredOut = isFilter;
            Options.Tasks.IncludeUnselected = isManual;
            // Tasks.TomoFullReconstructPixel : Pixel size : 10.0
            // Tasks.InputInvert : Invert Constrast : true
            // Tasks.InputNormalize : Normalize input images : true
            // Tasks.TomoFullReconstructDoDeconv : Also produce deconvolved version : true
            // Tasks.TomoFullReconstructDeconvStrength : Strength : 1.00
            // Tasks.TomoFullReconstructDeconvFalloff : Falloff : 1.00
            // Tasks.TomoFullReconstructDeconvHighpass : High-pass : 300
            // Tasks.TomoFullReconstructPrepareDenoising : Separate odd/even tilts for denoising. : false
            // Tasks.TomoFullReconstructOnlyFullVoxels : Keep only fully covered voxels : false
            bool Filter = isFilter; // Include items outside of filter ranges. True // Tasks.IncludeFilteredOut
            bool Manual = isManual; // Includemanually excluded items. False. // Tasks.IncludeUnselected

            #region Get all movies that can potentially be used

            List<TiltSeries> ValidSeries = Series.Where(v =>
            {
                if (!Filter && v.UnselectFilter && v.UnselectManual == null)
                    return false;
                if (!Manual && v.UnselectManual != null && (bool)v.UnselectManual)
                    return false;
                if (v.OptionsCTF == null)
                    return false;
                return true;
            }).ToList();

            Console.WriteLine($"Valid series count : {ValidSeries.Count}");
            if (ValidSeries.Count == 0)
                return;

            #endregion

            #region Set up progress displays

            NParallel = Math.Min(ValidSeries.Count, GPU.GetDeviceCount());

            #endregion

            int Completed = 0;

            
            Helper.ForEachGPU(ValidSeries, (item, gpuID) =>
                {
                    ProcessingOptionsTomoFullReconstruction SeriesOptions = Options.GetProcessingTomoFullReconstruction();

                    item.ReconstructFull(SeriesOptions, (size, value, name) =>
                    {
                        return IsCanceled;
                    });

                    ++Completed;
                    return false;   // No need to cancel GPU ForEach iterator
                }, 1);
        }
    }
}
