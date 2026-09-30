using System;
using System.Collections.Generic;
using System.Windows;

namespace CAIME
{
    /// <summary>
    /// Backs the upscale dialog: the user edits target width or height directly (aspect-locked - editing
    /// one derives a scale factor and re-derives the other), rather than typing an integer multiplier.
    /// Typical scale factors for keeping a map under <see cref="MAX_MAP_DIMENSION"/> are fractional
    /// (1.25x, 1.5x) since a flat 2x/3x almost always overshoots it.
    /// </summary>
    class UpscaleViewModel : BaseViewModel
    {
        public const uint MAX_MAP_DIMENSION = 2048;

        private readonly ProjectManager _projectManager;
        private readonly uint oldWidth;
        private readonly uint oldHeight;

        public string OldWidth  => oldWidth.ToString();
        public string OldHeight => oldHeight.ToString();

        private double factor = 1.0;
        public string FactorDisplay => factor.ToString("0.####") + "x";

        private const int PRESERVE_STRUCTURE_INDEX = 0;
        public List<string> Methods { get; } = new List<string> { "Preserve structure", "Nearest neighbour" };
        public int SelectedMethodIndex { get; set; } = PRESERVE_STRUCTURE_INDEX;

        private uint newWidth;
        public string NewWidth
        {
            get
            {
                return newWidth.ToString();
            }
            set
            {
                if (!uint.TryParse(value, out uint parsed) || parsed < 1)
                {
                    LoggerViewModel.Log("New width should be a positive integer!", LogLevel.Error);
                    MessageBox.Show("Error. New width should be a positive integer!", "Parsing error");
                    return;
                }

                double newFactor = parsed / (double)oldWidth;
                if (!TryApplyFactor(newFactor, out _, out uint derivedHeight))
                {
                    return;
                }

                factor    = newFactor;
                newWidth  = parsed;
                newHeight = derivedHeight;
                RaiseSizeChanged();
            }
        }

        private uint newHeight;
        public string NewHeight
        {
            get
            {
                return newHeight.ToString();
            }
            set
            {
                if (!uint.TryParse(value, out uint parsed) || parsed < 1)
                {
                    LoggerViewModel.Log("New height should be a positive integer!", LogLevel.Error);
                    MessageBox.Show("Error. New height should be a positive integer!", "Parsing error");
                    return;
                }

                double newFactor = parsed / (double)oldHeight;
                if (!TryApplyFactor(newFactor, out uint derivedWidth, out _))
                {
                    return;
                }

                factor    = newFactor;
                newHeight = parsed;
                newWidth  = derivedWidth;
                RaiseSizeChanged();
            }
        }

        public UpscaleViewModel(ProjectManager projectManager)
        {
            _projectManager = projectManager;
            oldWidth        = projectManager.Project.MapWidth;
            oldHeight       = projectManager.Project.MapHeight;
            newWidth        = oldWidth;
            newHeight       = oldHeight;
        }

        /// <summary>
        /// Validates that applying <paramref name="candidateFactor"/> to both dimensions keeps both
        /// under <see cref="MAX_MAP_DIMENSION"/>, returning both derived dimensions on success.
        /// </summary>
        private bool TryApplyFactor(double candidateFactor, out uint derivedWidth, out uint derivedHeight)
        {
            derivedWidth  = (uint)Math.Max(1, Math.Round(oldWidth  * candidateFactor));
            derivedHeight = (uint)Math.Max(1, Math.Round(oldHeight * candidateFactor));

            if (derivedWidth > MAX_MAP_DIMENSION || derivedHeight > MAX_MAP_DIMENSION)
            {
                LoggerViewModel.Log($"That size would exceed the maximum map size of {MAX_MAP_DIMENSION}x{MAX_MAP_DIMENSION}!", LogLevel.Error);
                MessageBox.Show($"Error. That size would exceed the maximum map size of {MAX_MAP_DIMENSION}x{MAX_MAP_DIMENSION}!", "Parsing error");
                return false;
            }

            return true;
        }

        private void RaiseSizeChanged()
        {
            OnPropertyChanged(nameof(NewWidth));
            OnPropertyChanged(nameof(NewHeight));
            OnPropertyChanged(nameof(FactorDisplay));
        }

        public bool UpscaleProject()
        {
            if (factor <= 1.0)
            {
                LoggerViewModel.Log("New size must be larger than the current size to upscale!", LogLevel.Error);
                MessageBox.Show("Error. New size must be larger than the current size to upscale!", "Parsing error");
                return false;
            }

            _projectManager.UpscaleProject(factor, preserveStructure: SelectedMethodIndex == PRESERVE_STRUCTURE_INDEX);
            return true;
        }
    }
}
