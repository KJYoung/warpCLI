using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Warp.Headers;
using Warp.Tools;

namespace Warp
{
    /// <summary>
    /// Interaction logic for DialogParticleExport3D.xaml
    /// </summary>
    public partial class TomoParticleExport
    {
        public TiltSeries[] Series;
        public string ImportPath, ExportPath;
        
        string InputSuffix = "";
        string InputFolder = "";

        List<float> Timings = new List<float>();

        Options Options; 
        public TomoParticleExport(TiltSeries[] series, string importPath, Options options)
        {
            Console.WriteLine("import path is {0}", importPath);
            
            Series = series;
            ImportPath = importPath;
            Options = options;

            if (series.Any(m => m.OptionsCTF != null))
            {
                Options.Tasks.InputPixelSize = series.First(m => m.OptionsCTF != null).OptionsCTF.BinnedPixelSizeMean;
            }
            else
            {
                Options.Tasks.Export2DPreflip = false;
                //CheckPreflip.IsEnabled = false;
            }

            #region Check if using different input files for each item makes sense

            bool FoundMatchingPrefix = false;
            string ImportName = Helper.PathToNameWithExtension(importPath);
            foreach (var item in Series)
            {
                if (ImportName.Contains(item.RootName))
                {
                    FoundMatchingPrefix = true;
                    InputSuffix = ImportName.Substring(item.RootName.Length);
                    break;
                }
            }

            if (FoundMatchingPrefix)
            {
                Options.Tasks.InputOnePerItem = true;

                FileInfo Info = new FileInfo(importPath);
                InputFolder = Info.DirectoryName;
                if (InputFolder.Last() != '/' && InputFolder.Last() != '\\')
                    InputFolder += "/";
            }
            else
            {
                Options.Tasks.InputOnePerItem = false;
            }

            #endregion

        }

        public async Task WorkStart()
        {
            Console.WriteLine("Work Started in TomoParticleExport.cs");
            ExportPath = "/cdata/relion/subtomograms_1.6Apx.star";
            
            bool[] boolOptions = { true , true , true, true, true, false, true, false };

            bool Invert = boolOptions[0]; //T
            bool Normalize = boolOptions[1]; //T
            bool Preflip = boolOptions[2]; //T
            bool Relative = boolOptions[3]; //T
            bool Filter = boolOptions[4]; //T
            bool Manual = boolOptions[5]; //F

            int BoxSize = (int)Options.Tasks.Export2DBoxSize;
            int NormDiameter = (int)Options.Tasks.Export2DParticleDiameter;

            bool DoVolumes = boolOptions[6]; //T
            if (!DoVolumes)
                Options.Tasks.TomoSubReconstructPrerotated = true;
            bool MakeSparse = boolOptions[7]; //F
            float3 AdditionalShiftAngstrom = new float3(0);

            Console.WriteLine($" Boxsize : {BoxSize} / NormDiameter : {NormDiameter}");
            //float3 AdditionalShiftAngstrom = (bool)CheckShiftParticles.IsChecked ? 
            //                                 new float3((float)SliderShiftParticlesX.Value,
            //                                            (float)SliderShiftParticlesY.Value,
            //                                            (float)SliderShiftParticlesZ.Value) : 
            //                                 new float3(0);
            await Task.Run(() =>
            {
                #region Get all movies that can potentially be used

                List<TiltSeries> ValidSeries = Series.Where(v =>
                {

                    if (!Filter && v.UnselectFilter && v.UnselectManual == null){
                        Console.WriteLine("{0}, {1}, {2}", Filter, v.UnselectFilter, v.UnselectManual);
                        return false;
                    }
                    if (!Manual && v.UnselectManual != null && (bool)v.UnselectManual){
                        Console.WriteLine("[2] {0}, {1}, {2}", Filter, v.UnselectFilter, v.UnselectManual);
                        return false;
                    }
                    if (v.OptionsCTF == null){
                        Console.WriteLine("{0}, {1}, {2}, {3}", Filter, v.UnselectFilter, v.UnselectManual, v.OptionsCTF);
                        return false;
                    }
                    return true;
                }).ToList();
                List<string> ValidMovieNames = ValidSeries.Select(m => m.RootName).ToList();

                #endregion

                #region Read table and intersect its micrograph set with valid movies

                Star TableIn;

                if (Options.Tasks.InputOnePerItem)
                {
                    List<Star> Tables = new List<Star>();
                    foreach (var item in Series)
                    {
                        string StarPath = InputFolder + item.RootName + InputSuffix;
                        if (File.Exists(StarPath))
                        {
                            Star TableItem = new Star(StarPath);
                            if (!TableItem.HasColumn("rlnMicrographName"))
                                TableItem.AddColumn("rlnMicrographName", item.Name);
                            else
                                TableItem.SetColumn("rlnMicrographName", Helper.ArrayOfConstant(item.Name, TableItem.RowCount));

                            Tables.Add(TableItem);
                        }
                    }

                    TableIn = new Star(Tables.ToArray());
                }
                else
                {
                    TableIn = new Star(ImportPath);
                }

                
                if (!TableIn.HasColumn("rlnMicrographName"))
                    throw new Exception("Couldn't find rlnMicrographName column.");
                if (!TableIn.HasColumn("rlnCoordinateX"))
                    throw new Exception("Couldn't find rlnCoordinateX column.");
                if (!TableIn.HasColumn("rlnCoordinateY"))
                    throw new Exception("Couldn't find rlnCoordinateY column.");
                if (!TableIn.HasColumn("rlnCoordinateZ"))
                    throw new Exception("Couldn't find rlnCoordinateZ column.");

                Dictionary<string, List<int>> Groups = new Dictionary<string, List<int>>();
                {
                    string[] ColumnMicNames = TableIn.GetColumn("rlnMicrographName");
                    for (int r = 0; r < ColumnMicNames.Length; r++)
                    {
                        if (!Groups.ContainsKey(ColumnMicNames[r]))
                            Groups.Add(ColumnMicNames[r], new List<int>());
                        Groups[ColumnMicNames[r]].Add(r);
                    }
                    Groups = Groups.ToDictionary(group => Helper.PathToName(group.Key), group => group.Value);

                    Groups = Groups.Where(group => ValidMovieNames.Any(n => group.Key.Contains(n))).ToDictionary(group => group.Key, group => group.Value);
                }

                bool[] RowsIncluded = new bool[TableIn.RowCount];
                foreach (var group in Groups)
                foreach (var r in group.Value)
                    RowsIncluded[r] = true;
                List<int> RowsNotIncluded = new List<int>();
                for (int r = 0; r < RowsIncluded.Length; r++)
                    if (!RowsIncluded[r])
                        RowsNotIncluded.Add(r);

                ValidSeries = ValidSeries.Where(v => Groups.Any(n => n.Key.Contains(v.RootName))).ToList();

                if (ValidSeries.Count == 0){     // Exit if there is nothing to export, otherwise errors will be thrown below
                    Console.WriteLine("No Valid Series!!!!");
                    return;
                }
                #endregion

                #region Make sure all columns are there

                if (!TableIn.HasColumn("rlnMagnification"))
                    TableIn.AddColumn("rlnMagnification", "10000.0");
                else
                    TableIn.SetColumn("rlnMagnification", Helper.ArrayOfConstant("10000.0", TableIn.RowCount));

                if (!TableIn.HasColumn("rlnDetectorPixelSize"))
                    TableIn.AddColumn("rlnDetectorPixelSize", Options.Tasks.TomoSubReconstructPixel.ToString("F5", CultureInfo.InvariantCulture));
                else
                    TableIn.SetColumn("rlnDetectorPixelSize", Helper.ArrayOfConstant(Options.Tasks.TomoSubReconstructPixel.ToString("F5", CultureInfo.InvariantCulture), TableIn.RowCount));

                if (!TableIn.HasColumn("rlnCtfMaxResolution"))
                    TableIn.AddColumn("rlnCtfMaxResolution", "999.0");

                if (!TableIn.HasColumn("rlnImageName"))
                    TableIn.AddColumn("rlnImageName", "None");

                if (!TableIn.HasColumn("rlnCtfImage"))
                    TableIn.AddColumn("rlnCtfImage", "None");

                List<Star> SeriesTablesOut = new List<Star>();

                #endregion
                
                #region Create worker processes

                int NDevices = GPU.GetDeviceCount();
                List<int> UsedDevices = Options.MainWindow.GetDeviceList();
                List<int> UsedDeviceProcesses = Helper.Combine(Helper.ArrayOfFunction(i => UsedDevices.Select(d => d + i * NDevices).ToArray(), 1)).ToList();
                // MainWindow.GlobalOptions.ProcessesPerDevice set to 1 VKJY.

                WorkerWrapper[] Workers = new WorkerWrapper[GPU.GetDeviceCount() * 1];
                
                Console.WriteLine($"Right before the first worker stage! {NDevices}, {UsedDeviceProcesses.Count}");
                foreach (var gpuID in UsedDeviceProcesses)
                {
                    Workers[gpuID] = new WorkerWrapper(gpuID);
                    Workers[gpuID].SetHeaderlessParams(new int2(Options.Import.HeaderlessWidth, Options.Import.HeaderlessHeight),
                                                        Options.Import.HeaderlessOffset,
                                                        Options.Import.HeaderlessType);

                    Workers[gpuID].LoadGainRef(Options.Import.CorrectGain ? Options.Import.GainPath : "",
                                               Options.Import.GainFlipX,
                                               Options.Import.GainFlipY,
                                               Options.Import.GainTranspose,
                                               Options.Import.CorrectDefects ? Options.Import.DefectsPath : "");
                }

                #endregion

                Star TableOut = null;
                {
                    Dictionary<string, Star> MicrographTables = new Dictionary<string, Star>();

                    #region Get coordinates and angles

                    float[] PosX = TableIn.GetColumn("rlnCoordinateX").Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray();
                    float[] PosY = TableIn.GetColumn("rlnCoordinateY").Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray();
                    float[] PosZ = TableIn.GetColumn("rlnCoordinateZ").Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray();
                    float[] ShiftX = TableIn.HasColumn("rlnOriginX") ? TableIn.GetColumn("rlnOriginX").Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray() : new float[TableIn.RowCount];
                    float[] ShiftY = TableIn.HasColumn("rlnOriginY") ? TableIn.GetColumn("rlnOriginY").Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray() : new float[TableIn.RowCount];
                    float[] ShiftZ = TableIn.HasColumn("rlnOriginZ") ? TableIn.GetColumn("rlnOriginZ").Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray() : new float[TableIn.RowCount];
                    

                    if (Options.Tasks.TomoSubReconstructNormalizedCoords)
                    {
                        for (int r = 0; r < TableIn.RowCount; r++)
                        {
                            PosX[r] *= (float)Options.Tomo.DimensionsX * (float)Options.PixelSizeMean;
                            PosY[r] *= (float)Options.Tomo.DimensionsY * (float)Options.PixelSizeMean;
                            PosZ[r] *= (float)Options.Tomo.DimensionsZ * (float)Options.PixelSizeMean;
                        }
                    }
                    else
                    {
                        for (int r = 0; r < TableIn.RowCount; r++)
                        {
                            PosX[r] = (PosX[r] - ShiftX[r]) * (float)Options.Tasks.InputPixelSize;
                            PosY[r] = (PosY[r] - ShiftY[r]) * (float)Options.Tasks.InputPixelSize;
                            PosZ[r] = (PosZ[r] - ShiftZ[r]) * (float)Options.Tasks.InputPixelSize;
                        }
                    }

                    float[] AngleRot = TableIn.HasColumn("rlnAngleRot") && Options.Tasks.TomoSubReconstructPrerotated ? TableIn.GetColumn("rlnAngleRot").Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray() : new float[TableIn.RowCount];
                    float[] AngleTilt = TableIn.HasColumn("rlnAngleTilt") && Options.Tasks.TomoSubReconstructPrerotated ? TableIn.GetColumn("rlnAngleTilt").Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray() : new float[TableIn.RowCount];
                    float[] AnglePsi = TableIn.HasColumn("rlnAnglePsi") && Options.Tasks.TomoSubReconstructPrerotated ? TableIn.GetColumn("rlnAnglePsi").Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray() : new float[TableIn.RowCount];

                    if (TableIn.HasColumn("rlnOriginX"))
                        TableIn.RemoveColumn("rlnOriginX");
                    if (TableIn.HasColumn("rlnOriginY"))
                        TableIn.RemoveColumn("rlnOriginY");
                    if (TableIn.HasColumn("rlnOriginZ"))
                        TableIn.RemoveColumn("rlnOriginZ");

                    if (AdditionalShiftAngstrom.Length() > 0)
                    {
                        for (int r = 0; r < TableIn.RowCount; r++)
                        {
                            Matrix3 R = Matrix3.Euler(AngleRot[r] * Helper.ToRad,
                                                      AngleTilt[r] * Helper.ToRad,
                                                      AnglePsi[r] * Helper.ToRad);
                            float3 RotatedShift = R * AdditionalShiftAngstrom;

                            PosX[r] += RotatedShift.X;
                            PosY[r] += RotatedShift.Y;
                            PosZ[r] += RotatedShift.Z;
                        }
                    }

                    if (Options.Tasks.TomoSubReconstructPrerotated)
                    {
                        if (TableIn.HasColumn("rlnAngleRot"))
                        {
                            TableIn.RemoveColumn("rlnAngleRot");
                            TableIn.AddColumn("rlnAngleRot", "0");
                        }
                        if (TableIn.HasColumn("rlnAngleTilt"))
                        {
                            TableIn.RemoveColumn("rlnAngleTilt");
                            TableIn.AddColumn("rlnAngleTilt", "0");
                        }
                        if (TableIn.HasColumn("rlnAnglePsi"))
                        {
                            TableIn.RemoveColumn("rlnAnglePsi");
                            TableIn.AddColumn("rlnAnglePsi", "0");
                        }
                    }

                    #endregion

                    Console.WriteLine("Right before the second worker stage!");
                    Helper.ForEachGPU(ValidSeries, (series, gpuID) =>
                    {

                        Stopwatch ItemTime = new Stopwatch();
                        ItemTime.Start();
                        
                        //ProcessingOptionsTomoSubReconstruction ExportOptions = Options.GetProcessingTomoSubReconstruction();
                        ProcessingOptionsTomoSubReconstruction ExportOptions = new ProcessingOptionsTomoSubReconstruction
                                    {
                                        PixelSizeX = 0.6750M,
                                        PixelSizeY = 0.6750M,
                                        PixelSizeAngle = 0,

                                        BinTimes = 1.24511249783653M,
                                        EERGroupFrames = 0,
                                        GainPath = "",
                                        GainHash = "",
                                        DefectsPath = "",
                                        DefectsHash = "",
                                        GainFlipX = false,
                                        GainFlipY = false,
                                        GainTranspose = false,

                                        Dimensions = new float3((float)8000,
                                                                (float)8000,
                                                                (float)6000),

                                        Suffix = "",

                                        BoxSize = (int)128,
                                        ParticleDiameter = (int)160,

                                        Invert = true,
                                        NormalizeInput = true,
                                        NormalizeOutput = true,

                                        PrerotateParticles = false,
                                        DoLimitDose = false,
                                        NTilts = 1,

                                        MakeSparse = false
                                    };
                        #region Update row values

                        List<int> GroupRows = Groups.First(n => n.Key.Contains(series.RootName)).Value;

                        int pi = 0;
                        foreach (var r in GroupRows)
                        {
                            TableIn.SetRowValue(r, "rlnCtfMaxResolution", series.CTFResolutionEstimate.ToString("F1", CultureInfo.InvariantCulture));

                            TableIn.SetRowValue(r, "rlnCoordinateX", (PosX[r] / (float)ExportOptions.BinnedPixelSizeMean).ToString("F3", CultureInfo.InvariantCulture));
                            TableIn.SetRowValue(r, "rlnCoordinateY", (PosY[r] / (float)ExportOptions.BinnedPixelSizeMean).ToString("F3", CultureInfo.InvariantCulture));
                            TableIn.SetRowValue(r, "rlnCoordinateZ", (PosZ[r] / (float)ExportOptions.BinnedPixelSizeMean).ToString("F3", CultureInfo.InvariantCulture));

                            #region Figure out relative or absolute path to sub-tomo and its CTF

                            string PathSubtomo = series.SubtomoDir + $"{series.RootName}{ExportOptions.Suffix}_{pi:D7}_{ExportOptions.BinnedPixelSizeMean:F2}A.mrc";
                            string PathCTF = //MakeSparse ?
                                            //(series.SubtomoDir + $"{series.RootName}_{pi:D7}_ctf_{ExportOptions.BinnedPixelSizeMean:F2}A.tif") :
                                            (series.SubtomoDir + $"{series.RootName}{ExportOptions.Suffix}_{pi:D7}_ctf_{ExportOptions.BinnedPixelSizeMean:F2}A.mrc");
                            if (Relative)
                            {
                                Uri UriStar = new Uri(ExportPath);
                                PathSubtomo = UriStar.MakeRelativeUri(new Uri(PathSubtomo)).ToString();
                                PathCTF = UriStar.MakeRelativeUri(new Uri(PathCTF)).ToString();
                            }

                            #endregion

                            TableIn.SetRowValue(r, "rlnImageName", PathSubtomo);
                            TableIn.SetRowValue(r, "rlnCtfImage", PathCTF);

                            pi++;
                        }

                        #endregion

                        #region Populate micrograph table with rows for all exported particles

                        Star MicrographTable = new Star(TableIn.GetColumnNames());
                        
                        foreach (var r in GroupRows)
                            MicrographTable.AddRow(TableIn.GetRow(r).ToList());

                        #endregion

                        #region Finally, reconstruct the actual sub-tomos

                        float3[] TomoPositions = Helper.Combine(GroupRows.Select(r => Helper.ArrayOfConstant(new float3(PosX[r], PosY[r], PosZ[r]), series.NTilts)).ToArray());
                        float3[] TomoAngles = Helper.Combine(GroupRows.Select(r => Helper.ArrayOfConstant(new float3(AngleRot[r], AngleTilt[r], AnglePsi[r]), series.NTilts)).ToArray());

                        if (DoVolumes)
                        {
                            Console.WriteLine($"------------------PRINT PARAMETER---------------------\n{ExportOptions.PixelSizeX}/{ExportOptions.PixelSizeY}/{ExportOptions.PixelSizeAngle}\n" +
                                $"{ExportOptions.BinTimes}/{ExportOptions.EERGroupFrames}/{ExportOptions.GainPath}/{ExportOptions.GainHash}\n" +
                                $"{ExportOptions.DefectsPath}/{ExportOptions.DefectsHash}/{ExportOptions.GainFlipX}/{ExportOptions.GainFlipY}\n" +
                                $"{ExportOptions.GainTranspose}/{ExportOptions.Dimensions.X}/{ExportOptions.Dimensions.Y}/{ExportOptions.Dimensions.Z}\n" +
                                $"{ExportOptions.Suffix}/{ExportOptions.BoxSize}/{ExportOptions.ParticleDiameter}/{ExportOptions.Invert}\n" +
                                $"{ExportOptions.NormalizeInput}/{ExportOptions.NormalizeOutput}/{ExportOptions.PrerotateParticles}\n" +
                                $"{ExportOptions.DoLimitDose}/{ExportOptions.NTilts}/{ExportOptions.MakeSparse}");

                            Console.WriteLine($"------------------PRINT PARAMETER2---------------------\n{TomoPositions.Length}/{TomoAngles.Length}");
                            
                            
                            Workers[gpuID].TomoExportParticles(series.Path, ExportOptions, TomoPositions, TomoAngles);
                            //Console.WriteLine("tomo execution with : {0}, {1}, {2}, {3}", series.Path, ExportOptions, TomoPositions, TomoAngles);
                            ////series.ReconstructSubtomos(ExportOptions, TomoPositions, TomoAngles);

                            lock (MicrographTables)
                                MicrographTables.Add(series.RootName, MicrographTable);
                        }
                        else
                        {
                            Star SeriesTable;
                            Random Rand = new Random(123);
                            int[] Subsets = Helper.ArrayOfFunction(i => Rand.Next(1, 3), GroupRows.Count);
                            series.ReconstructParticleSeries(ExportOptions, TomoPositions, TomoAngles, Subsets, ExportPath, out SeriesTable);

                            lock (MicrographTables)
                                MicrographTables.Add(series.RootName, SeriesTable);
                        }

                        #endregion

                        #region Add this micrograph's table to global collection, update remaining time estimate

                        lock (MicrographTables)
                        {
                            Timings.Add(ItemTime.ElapsedMilliseconds / (float)UsedDeviceProcesses.Count);

                            int MsRemaining = (int)(MathHelper.Mean(Timings) * (ValidSeries.Count - MicrographTables.Count));
                            TimeSpan SpanRemaining = new TimeSpan(0, 0, 0, 0, MsRemaining);

                        }

                        #endregion

                    }, 1, UsedDeviceProcesses);

                    if (MicrographTables.Count > 0)
                        TableOut = new Star(MicrographTables.Values.ToArray());
                }
                Console.WriteLine("---------- Procedure Ended Waiting for writing... ------------");
                Thread.Sleep(10000);    // Writing out particles is async, so if workers are killed immediately they may not write out everything

                foreach (var worker in Workers)
                     worker?.Dispose();

                TableOut.Save(ExportPath);
            });
        }
    }
}
