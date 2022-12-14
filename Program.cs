using System;
using System.Linq; 
/* Linq for the error : error CS1061: 'Movie[]' does not contain a definition for 'Cast' and no accessible extension method 'Cast' accepting a first argument of type 'Movie[]' could be found (are you missing a using directive or an assembly reference?)
   Refer : https://docs.microsoft.com/ko-kr/dotnet/api/system.linq.enumerable.cast?view=net-6.0
*/ 
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using Warp;
using Warp.Tools;
using Warp.Sociology;
using Warp.Headers;

using System.Collections.ObjectModel;
using System.Diagnostics.Contracts; 
using System.IO;
using System.Text;
using System.Xml;
using System.Xml.XPath;
using System.Globalization; // for CultureInfo.

using Warp.Controls.TaskDialogs.Tomo;
using Warp.Controls;

namespace Warp{
    // From Controls.StatusBar.xaml.cs
    enum ProcessingStatus
    {
        Processed = 1,
        Outdated = 2,
        Unprocessed = 3,
        FilteredOut = 4,
        LeaveOut = 5
    }
    // From Controls.SingleAxisScatter.xaml.cs
    public struct SingleAxisPoint
    {
        public double Value;
        public int ColorID;
        public Movie Context;

        public SingleAxisPoint(double value, int colorID, Movie context)
        {
            Value = value;
            ColorID = colorID;
            Context = context;
        }
    }
    // From Controls.DualAxisScatter.xaml.cs
    public struct DualAxisPoint
    {
        public double X, Y;
        public int ColorID;
        public Movie Context;
        public string Label;

        public DualAxisPoint(double x, double y, int colorID, Movie context, string label)
        {
            X = x;
            Y = y;
            ColorID = colorID;
            Context = context;
            Label = label;
        }
    }

    public class Program{
        public static GlobalOptions GlobalOptions = new GlobalOptions();

        private BenchmarkTimer BenchmarkRead = new BenchmarkTimer("File read");
        private BenchmarkTimer BenchmarkCTF = new BenchmarkTimer("CTF");
        private BenchmarkTimer BenchmarkMotion = new BenchmarkTimer("Motion");
        private BenchmarkTimer BenchmarkPicking = new BenchmarkTimer("Picking");
        private BenchmarkTimer BenchmarkOutput = new BenchmarkTimer("Output");

        private BenchmarkTimer BenchmarkAllProcessing = new BenchmarkTimer("All processing");

        #region Helper methods
        public static Image LoadAndPrepareGainReference()
        {
            Image Gain = Image.FromFilePatient(50, 500,
                                               Options.Import.GainPath,
                                               new int2(Options.Import.HeaderlessWidth, Options.Import.HeaderlessHeight),
                                               (int)Options.Import.HeaderlessOffset,
                                               ImageFormatsHelper.StringToType(Options.Import.HeaderlessType));

            float Mean = MathHelper.Mean(Gain.GetHost(Intent.Read)[0]);
            Gain.TransformValues(v => v == 0 ? 1 : v / Mean);

            if (Options.Import.GainFlipX)
                Gain = Gain.AsFlippedX();
            if (Options.Import.GainFlipY)
                Gain = Gain.AsFlippedY();
            if (Options.Import.GainTranspose)
                Gain = Gain.AsTransposed();

            return Gain;
        }
        public static DefectModel LoadAndPrepareDefectMap()
        {
            Image Defects = Image.FromFilePatient(50, 500,
                                                  Options.Import.DefectsPath,
                                                  new int2(Options.Import.HeaderlessWidth, Options.Import.HeaderlessHeight),
                                                  (int)Options.Import.HeaderlessOffset,
                                                  ImageFormatsHelper.StringToType(Options.Import.HeaderlessType));

            if (Options.Import.GainFlipX)
                Defects = Defects.AsFlippedX();
            if (Options.Import.GainFlipY)
                Defects = Defects.AsFlippedY();
            if (Options.Import.GainTranspose)
                Defects = Defects.AsTransposed();

            DefectModel Model = new DefectModel(Defects, 4);
            Defects.Dispose();

            return Model;
        }

        public static void LoadAndPrepareHeaderAndMap(string path, Image imageGain, DefectModel defectMap, decimal scaleFactor, out MapHeader header, out Image stack, bool needStack = true, int maxThreads = 8)
        {
            HeaderEER.GroupNFrames = Options.Import.EERGroupFrames;

            header = MapHeader.ReadFromFilePatient(50, 500,
                                                   path,
                                                   new int2(Options.Import.HeaderlessWidth, Options.Import.HeaderlessHeight),
                                                   Options.Import.HeaderlessOffset,
                                                   ImageFormatsHelper.StringToType(Options.Import.HeaderlessType));

            string Extension = Helper.PathToExtension(path).ToLower();
            bool IsTiff = header.GetType() == typeof(HeaderTiff);
            bool IsEER = header.GetType() == typeof(HeaderEER);

            if (imageGain != null)
                if (!IsEER)
                    if (header.Dimensions.X != imageGain.Dims.X || header.Dimensions.Y != imageGain.Dims.Y)
                        throw new Exception("Gain reference dimensions do not match image.");

            int EERSupersample = 1;
            if (imageGain != null && IsEER)
            {
                if (header.Dimensions.X == imageGain.Dims.X)
                    EERSupersample = 1;
                else if (header.Dimensions.X * 2 == imageGain.Dims.X)
                    EERSupersample = 2;
                else if (header.Dimensions.X * 4 == imageGain.Dims.X)
                    EERSupersample = 3;
                else
                    throw new Exception("Invalid supersampling factor requested for EER based on gain reference dimensions");
            }

            HeaderEER.SuperResolution = EERSupersample;

            if (IsEER && imageGain != null)
            {
                header.Dimensions.X = imageGain.Dims.X;
                header.Dimensions.Y = imageGain.Dims.Y;
            }
            MapHeader Header = header;

            int NThreads = (IsTiff || IsEER) ? 6 : 2;

            int CurrentDevice = GPU.GetDevice();

            if (needStack)
            {
                byte[] TiffBytes = null;
                if (IsTiff)
                {
                    MemoryStream Stream = new MemoryStream();
                    using (Stream BigBufferStream = IOHelper.OpenWithBigBuffer(path))
                        BigBufferStream.CopyTo(Stream);
                    TiffBytes = Stream.GetBuffer();
                }

                if (scaleFactor == 1M)
                {
                    stack = new Image(header.Dimensions);
                    float[][] OriginalStackData = stack.GetHost(Intent.Write);

                    Helper.ForCPU(0, header.Dimensions.Z, NThreads, threadID => GPU.SetDevice(CurrentDevice), (z, threadID) =>
                    {
                        Image Layer = null;
                        MemoryStream TiffStream = TiffBytes != null ? new MemoryStream(TiffBytes) : null;

                        if (!IsEER)
                            Layer = Image.FromFilePatient(50, 500,
                                                        path,
                                                        new int2(Options.Import.HeaderlessWidth, Options.Import.HeaderlessHeight),
                                                        (int)Options.Import.HeaderlessOffset,
                                                        ImageFormatsHelper.StringToType(Options.Import.HeaderlessType),
                                                        z,
                                                        TiffStream);
                        else
                        {
                            Layer = new Image(Header.Dimensions.Slice());
                            EERNative.ReadEERPatient(50, 500,
                                                     path, z * 10, (z + 1) * 10, EERSupersample, Layer.GetHost(Intent.Write)[0]);
                        }

                        lock (OriginalStackData)
                        {
                            if (imageGain != null)
                            {
                                if (IsEER)
                                    Layer.DivideSlices(imageGain);
                                else
                                    Layer.MultiplySlices(imageGain);
                            }

                            if (defectMap != null)
                            {
                                Image LayerCopy = Layer.GetCopyGPU();
                                defectMap.Correct(LayerCopy, Layer);
                                LayerCopy.Dispose();
                            }

                            Layer.Xray(20f);

                            OriginalStackData[z] = Layer.GetHost(Intent.Read)[0];
                            Layer.Dispose();
                        }

                    }, null);
                }
                else
                {
                    int3 ScaledDims = new int3((int)Math.Round(header.Dimensions.X * scaleFactor) / 2 * 2,
                                               (int)Math.Round(header.Dimensions.Y * scaleFactor) / 2 * 2,
                                               header.Dimensions.Z);

                    stack = new Image(ScaledDims);
                    float[][] OriginalStackData = stack.GetHost(Intent.Write);

                    int PlanForw = GPU.CreateFFTPlan(header.Dimensions.Slice(), 1);
                    int PlanBack = GPU.CreateIFFTPlan(ScaledDims.Slice(), 1);

                    Helper.ForCPU(0, ScaledDims.Z, NThreads, threadID => GPU.SetDevice(CurrentDevice), (z, threadID) =>
                    {
                        Image Layer = null;
                        MemoryStream TiffStream = TiffBytes != null ? new MemoryStream(TiffBytes) : null;

                        if (!IsEER)
                            Layer = Image.FromFilePatient(50, 500,
                                                        path,
                                                        new int2(Options.Import.HeaderlessWidth, Options.Import.HeaderlessHeight),
                                                        (int)Options.Import.HeaderlessOffset,
                                                        ImageFormatsHelper.StringToType(Options.Import.HeaderlessType),
                                                        z,
                                                        TiffStream);
                        else
                        {
                            Layer = new Image(Header.Dimensions.Slice());
                            EERNative.ReadEERPatient(50, 500,
                                path, z * 10, (z + 1) * 10, EERSupersample, Layer.GetHost(Intent.Write)[0]);
                        }

                        Image ScaledLayer = null;
                        lock (OriginalStackData)
                        {
                            if (imageGain != null)
                            {
                                if (IsEER)
                                    Layer.DivideSlices(imageGain);
                                else
                                    Layer.MultiplySlices(imageGain);
                            }

                            if (defectMap != null)
                            {
                                Image LayerCopy = Layer.GetCopyGPU();
                                defectMap.Correct(LayerCopy, Layer);
                                LayerCopy.Dispose();
                            }

                            Layer.Xray(20f);

                            ScaledLayer = Layer.AsScaled(new int2(ScaledDims), PlanForw, PlanBack);
                            Layer.Dispose();
                        }

                        OriginalStackData[z] = ScaledLayer.GetHost(Intent.Read)[0];
                        ScaledLayer.Dispose();

                    }, null);

                    GPU.DestroyFFTPlan(PlanForw);
                    GPU.DestroyFFTPlan(PlanBack);
                }
            }
            else
            {
                stack = null;
            }
        }
        public string LocatePickingModel(string name)
        {
            // Console.WriteLine(Environment.CurrentDirectory);
            if (string.IsNullOrEmpty(name))
                return null;

            if (Directory.Exists(name))
            {
                return name;
            }
            else if (Directory.Exists(System.IO.Path.Combine(Environment.CurrentDirectory, "boxnet2models/" + name)))
            {
                return System.IO.Path.Combine(Environment.CurrentDirectory, "boxnet2models/" + name);
            }

            return null;
        }
        static ProcessingStatus GetMovieProcessingStatus(Movie movie, ProcessingOptionsMovieCTF optionsCTF, ProcessingOptionsMovieMovement optionsMovement, ProcessingOptionsBoxNet optionsBoxNet, ProcessingOptionsMovieExport optionsExport, Options options, bool considerFilter = true)
        {
            bool DoCTF = options.ProcessCTF;
            bool DoMovement = options.ProcessMovement;
            bool DoBoxNet = options.ProcessPicking;
            bool DoExport = optionsExport.DoAverage || optionsExport.DoStack || optionsExport.DoDeconv;
            
            ProcessingStatus Status = ProcessingStatus.Processed;

            if (movie.UnselectManual != null && (bool)movie.UnselectManual)
            {
                Status = ProcessingStatus.LeaveOut;
            }
            else if (movie.OptionsCTF == null && movie.OptionsMovement == null && movie.OptionsMovieExport == null)
            {
                Status = ProcessingStatus.Unprocessed;
            }
            else
            {
                if (DoCTF && (movie.OptionsCTF == null || movie.OptionsCTF != optionsCTF))
                    Status = ProcessingStatus.Outdated;
                else if (DoMovement && (movie.OptionsMovement == null || movie.OptionsMovement != optionsMovement))
                    Status = ProcessingStatus.Outdated;
                else if (DoBoxNet && (movie.OptionsBoxNet == null || movie.OptionsBoxNet != optionsBoxNet))
                    Status = ProcessingStatus.Outdated;
                else if (DoExport && (movie.OptionsMovieExport == null || movie.OptionsMovieExport != optionsExport))
                    Status = ProcessingStatus.Outdated;
            }

            if (Status == ProcessingStatus.Processed && movie.UnselectFilter && movie.UnselectManual == null && considerFilter)
                Status = ProcessingStatus.FilteredOut;

            return Status;
        }
        #endregion

        public readonly FileDiscoverer FileDiscoverer;
        string starPath, starFilePath;
        public static Options Options = new Options();

        public static bool IsPreprocessing = false;
        public static bool IsStoppingPreprocessing = false;
        static Task PreprocessingTask = null;

        bool IsPreprocessingCollapsed = false;
        int PreprocessingWidth = 450;

        // static string direcNameV = "/home/kimv/warpPort/" ; // or "/cdata/"
        static string direcNameV = "/cdata/"; // or "/cdata/"
        public async Task fileDiscovererReadyMRC(){
            FileDiscoverer.ChangePath(starPath, "*.mrc"); // CUSTOM VKJY
            await Task.Delay(500); // For wait to set FileDiscoverer!!!!
        }
        public async Task fileDiscovererReadyTOMOSTAR(){
            FileDiscoverer.ChangePath(starPath, "*.tomostar"); // CUSTOM VKJY
            await Task.Delay(2000); // For wait to set FileDiscoverer!!!!
        }

        TiltSeries[] getSeries(){
            TiltSeries[] Series = FileDiscoverer.GetImmutableFiles().Cast<TiltSeries>().ToArray();
            return Series;
        }
        // async Task tomoReconstruct(TiltSeries[] Series){
        //     Console.WriteLine("Enter the name of the *star file [ Should not include the .star ]");
        //     // starFilePath = Console.ReadLine() + ".star";
        //     starFilePath = "/cdata/relion/Refine3D/job002/run_data_rln3.0" + ".star";
        //     TomoParticleExport tpe = new TomoParticleExport(Series, starFilePath, Options);
        //     await tpe.WorkStart();
        // }
        private async Task ButtonProcessOneItemCTF_OnClick(Movie inputMovie)
        {
            if (inputMovie == null){
                Console.WriteLine("Input Movie is null.");
                return;
            }

            Movie Item = inputMovie;

            Console.WriteLine($"-Processing CTF for {Item.Name}...");

            await Task.Run(async () =>
            {
                Image ImageGain = null;
                DefectModel DefectMap = null;
                Image OriginalStack = null;

                HeaderEER.GroupNFrames = Options.Import.EERGroupFrames;

                try
                {
                    #region Get gain ref if needed

                    if (!string.IsNullOrEmpty(Options.Import.GainPath) && Options.Import.CorrectGain && File.Exists(Options.Import.GainPath))
                        ImageGain = LoadAndPrepareGainReference();

                    if (!string.IsNullOrEmpty(Options.Import.DefectsPath) && Options.Import.CorrectDefects && File.Exists(Options.Import.DefectsPath))
                        DefectMap = LoadAndPrepareDefectMap();

                    if (ImageGain != null && DefectMap != null)
                        if (ImageGain.Dims.X != DefectMap.Dims.X || ImageGain.Dims.Y != DefectMap.Dims.Y)
                            throw new Exception("Gain reference and defect map dimensions don't match");

                    #endregion

                    bool IsTomo = Item.GetType() == typeof(TiltSeries);

                    #region Load movie

                    MapHeader OriginalHeader = null;
                    decimal ScaleFactor = 1M / (decimal)Math.Pow(2, (double)Options.Import.BinTimes);

                    if (!IsTomo)
                        LoadAndPrepareHeaderAndMap(Item.Path, ImageGain, DefectMap, ScaleFactor, out OriginalHeader, out OriginalStack);

                    #endregion

                    ProcessingOptionsMovieCTF CurrentOptionsCTF = Options.GetProcessingMovieCTF();

                    // Store original dimensions in Angstrom
                    if (!IsTomo)
                    {
                        CurrentOptionsCTF.Dimensions = OriginalHeader.Dimensions.MultXY((float)Options.PixelSizeMean);
                    }
                    else
                    {
                        ((TiltSeries)Item).LoadMovieSizes(CurrentOptionsCTF);

                        float3 StackDims = new float3(((TiltSeries)Item).ImageDimensionsPhysical, ((TiltSeries)Item).NTilts);
                        CurrentOptionsCTF.Dimensions = StackDims;
                    }

                    if (Item.GetType() == typeof(Movie))
                        Item.ProcessCTF(OriginalStack, CurrentOptionsCTF);
                    else
                        ((TiltSeries)Item).ProcessCTFSimultaneous(CurrentOptionsCTF);

                    
                    // VKJY Dispatcher
                    // UpdateButtonOptionsAdopt();
                    // ProcessingStatusBar.UpdateElements();

                    UpdateStatsAll();

                    OriginalStack?.Dispose();
                    ImageGain?.Dispose();
                    DefectMap?.Dispose();

                    await Task.Delay(1000);
                }
                catch (Exception exc)
                {
                    ImageGain?.Dispose();
                    DefectMap?.Dispose();
                    OriginalStack?.Dispose();

                    Console.WriteLine("-There is an error in CTF estimation.");
                }
            });
        }

        private async Task ButtonProcessOneItemTiltHandedness_Click(Movie inputMovie)
        {
            if (inputMovie == null)
                return;

            TiltSeries Series = (TiltSeries)inputMovie;

            Console.WriteLine("Loading tilt movies and estimating gradients...");

            await Task.Run(async () =>
            {
                try
                {
                    Movie[] TiltMovies = Series.TiltMoviePaths.Select(s => new Movie(Path.Combine(Series.DirectoryName, s))).ToArray();

                    if (TiltMovies.Any(m => m.GridCTFDefocus.Values.Length < 2))
                        throw new Exception("One or more tilt movies don't have local defocus information.\n" +
                                            "Please run CTF estimation on all individual tilt movies with a 2x2 spatial resolution grid.");

                    Series.VolumeDimensionsPhysical = new float3((float)Options.Tomo.DimensionsX,
                                                                 (float)Options.Tomo.DimensionsY,
                                                                 (float)Options.Tomo.DimensionsZ) * (float)Options.PixelSizeMean;
                    Series.ImageDimensionsPhysical = new float2(Series.VolumeDimensionsPhysical.X, Series.VolumeDimensionsPhysical.Y);

                    float[] GradientsEstimated = new float[Series.NTilts];
                    float[] GradientsAssumed = new float[Series.NTilts];

                    float3[] Points = 
                    {
                        new float3(0, Series.VolumeDimensionsPhysical.Y / 2, Series.VolumeDimensionsPhysical.Z / 2),
                        new float3(Series.VolumeDimensionsPhysical.X, Series.VolumeDimensionsPhysical.Y / 2, Series.VolumeDimensionsPhysical.Z / 2)
                    };

                    float3[] Projected0 = Series.GetPositionInAllTilts(Points[0]).Select(v => v / new float3(Series.ImageDimensionsPhysical.X, Series.ImageDimensionsPhysical.Y, 1)).ToArray();
                    float3[] Projected1 = Series.GetPositionInAllTilts(Points[1]).Select(v => v / new float3(Series.ImageDimensionsPhysical.X, Series.ImageDimensionsPhysical.Y, 1)).ToArray();

                    for (int t = 0; t < Series.NTilts; t++)
                    {
                        float Interp0 = TiltMovies[t].GridCTFDefocus.GetInterpolated(new float3(Projected0[t].X, Projected0[0].Y, 0.5f));
                        float Interp1 = TiltMovies[t].GridCTFDefocus.GetInterpolated(new float3(Projected1[t].X, Projected1[0].Y, 0.5f));
                        GradientsEstimated[t] = Interp1 - Interp0;

                        GradientsAssumed[t] = Projected1[t].Z - Projected0[t].Z;
                    }

                    if (GradientsEstimated.Length > 1)
                    {
                        GradientsEstimated = MathHelper.Normalize(GradientsEstimated);
                        GradientsAssumed = MathHelper.Normalize(GradientsAssumed);
                    }
                    else
                    {
                        GradientsEstimated[0] = Math.Sign(GradientsEstimated[0]);
                        GradientsAssumed[0] = Math.Sign(GradientsAssumed[0]);
                    }

                    float Correlation = MathHelper.DotProduct(GradientsEstimated, GradientsAssumed) / GradientsEstimated.Length;

                    if (Correlation > 0)
                        Console.WriteLine($"It looks like the angles are in accord with the estimated defocus gradients. Correlation = {Correlation:F2}");
                    else
                    {
                        bool DoFlip = false;

                        Console.WriteLine("You're in the Upside Down!\n" + $"It looks like the defocus handedness should be flipped. Correlation = {Correlation:F2}\n" +
                                        "Would you like to flip it for all tilt series currently loaded?\n" +
                                        "You should probably repeat CTF estimation after flipping.");
                        Console.WriteLine("Type 1 for DoFlip, others for NotFlip");
                        int var = Convert.ToInt32(Console.ReadLine());
                        if(var == 1){
                            DoFlip = true;
                        }
                        if (DoFlip)
                        {
                            Console.WriteLine("-Do Flip : Saving tilt series metadata...");

                            TiltSeries[] AllSeries = FileDiscoverer.GetImmutableFiles().Select(m => (TiltSeries)m).ToArray();

                            for (int i = 0; i < AllSeries.Length; i++)
                            {
                                AllSeries[i].AreAnglesInverted = !AllSeries[i].AreAnglesInverted;
                                AllSeries[i].SaveMeta();
                            }
                        }else{
                            Console.WriteLine("-Not Flip...");
                        }
                    }
                    await Task.Delay(1500);
                }
                catch (Exception exc)
                {   
                    Console.WriteLine(exc.ToString());
                }
            });
        }

        private async Task Preprocessing()
        {
            if (!IsPreprocessing)
            {

                IsPreprocessing = true;

                bool IsTomo = Options.Import.ExtensionTomoSTAR;

                //PreprocessingTask = Task.Run(async () =>
                //{
                int NDevices = GPU.GetDeviceCount();
                List<int> UsedDevices = GetDeviceList();
                List<int> UsedDeviceProcesses = Helper.Combine(Helper.ArrayOfFunction(i => UsedDevices.Select(d => d + i * NDevices).ToArray(), GlobalOptions.ProcessesPerDevice)).ToList();

                Console.WriteLine("Section 1 : first part.");
                #region Check if options are compatible

                {
                    string ErrorMessage = "";
                }

                #endregion

                #region Load gain reference if needed

                Image ImageGain = null;
                DefectModel DefectMap = null;
                if (!string.IsNullOrEmpty(Options.Import.GainPath) && Options.Import.CorrectGain && File.Exists(Options.Import.GainPath))
                    try
                    {
                        ImageGain = LoadAndPrepareGainReference();
                    }
                    catch (Exception exc)
                    {
                        ImageGain?.Dispose();

                        Console.WriteLine("Oopsie",
                                                "Something went wrong when trying to load the gain reference.\n\n" +
                                                "The exception raised is:\n" + exc);

                        return;
                    }
                if (!string.IsNullOrEmpty(Options.Import.DefectsPath) && Options.Import.CorrectDefects && File.Exists(Options.Import.DefectsPath))
                    try
                    {
                        DefectMap = LoadAndPrepareDefectMap();

                        if (ImageGain != null && new int2(ImageGain.Dims) != DefectMap.Dims)
                            throw new Exception("Defect map and gain reference dimensions don't match.");
                    }
                    catch (Exception exc)
                    {
                        DefectMap?.Dispose();

                        Console.WriteLine("Oopsie",
                                                        "Something went wrong when trying to load the defect map.\n\n" +
                                                        "The exception raised is:\n" + exc);


                        return;
                    }

                #endregion

                Console.WriteLine("Section 2 : load model.");
                Console.WriteLine(IsTomo);
                Console.WriteLine(Options.ProcessPicking);
                #region Load BoxNet model if needed

                BoxNet2[] BoxNetworks = new BoxNet2[NDevices];
                object[] BoxNetLocks = Helper.ArrayOfFunction(i => new object(), NDevices);

                if (!IsTomo && Options.ProcessPicking)
                {

                    Image.FreeDeviceAll();

                    try
                    {
                        //await Dispatcher.Invoke(async () => ProgressDialog = await this.ShowProgressAsync($"Loading {Options.Picking.ModelPath} model...", ""));

                        if (string.IsNullOrEmpty(Options.Picking.ModelPath) || LocatePickingModel(Options.Picking.ModelPath) == null)
                            throw new Exception("No BoxNet model selected. Please use the options panel to select a model.");

                        // MicrographDisplayControl.DropBoxNetworks();
                        Console.WriteLine(UsedDevices);
                        foreach (var d in UsedDevices){
                            Console.WriteLine("FOREACH : " + d);
                            Console.WriteLine("RESULT : " + LocatePickingModel(Options.Picking.ModelPath));
                            BoxNetworks[d] = new BoxNet2(LocatePickingModel(Options.Picking.ModelPath), d, 2, 1, false);
                            //tftest1.tftest.joke2(1);
                        }
                    }
                    catch (Exception exc)
                    {
                        Console.WriteLine("There was an error loading the specified BoxNet model for picking.\n\n" + "The exception raised is:\n" + exc);

                        ImageGain?.Dispose();
                        DefectMap?.Dispose();

                        return;
                    }
                }

                #endregion
                
                Console.WriteLine("Section 3 : load or create STAR table.");
                #region Load or create STAR table for BoxNet output, if needed

                string BoxNetSuffix = Helper.PathToNameWithExtension(Options.Picking.ModelPath);

                Star TableBoxNetAll = null;
                string PathBoxNetAll = Options.Import.Folder + "allparticles_" + BoxNetSuffix + ".star";
                string PathBoxNetAllSubset = Options.Import.Folder + "allparticles_last" + Options.Picking.RunningWindowLength + "_" + BoxNetSuffix + ".star";
                string PathBoxNetFiltered = Options.Import.Folder + "goodparticles_" + BoxNetSuffix + ".star";
                string PathBoxNetFilteredSubset = Options.Import.Folder + "goodparticles_last" + Options.Picking.RunningWindowLength + "_" + BoxNetSuffix + ".star";
                object TableBoxNetAllWriteLock = new object();
                int TableBoxNetConcurrent = 0;

                Dictionary<Movie, List<List<string>>> AllMovieParticleRows = new Dictionary<Movie, List<List<string>>>();

                if (!IsTomo && Options.ProcessPicking && Options.Picking.DoExport && !string.IsNullOrEmpty(Options.Picking.ModelPath))
                {
                    Movie[] TempMovies = FileDiscoverer.GetImmutableFiles();

                    if (File.Exists(PathBoxNetAll))
                    {
                        Console.WriteLine("Loading particle metadata from previous run...");

                        TableBoxNetAll = new Star(PathBoxNetAll);

                        Dictionary<string, Movie> NameMapping = new Dictionary<string, Movie>();
                        string[] ColumnMicName = TableBoxNetAll.GetColumn("rlnMicrographName");
                        for (int r = 0; r < ColumnMicName.Length; r++)
                        {
                            if (!NameMapping.ContainsKey(ColumnMicName[r]))
                            {
                                var Movie = TempMovies.Where(m => ColumnMicName[r].Contains(m.Name));
                                if (Movie.Count() != 1)
                                    continue;

                                NameMapping.Add(ColumnMicName[r], Movie.First());
                                AllMovieParticleRows.Add(Movie.First(), new List<List<string>>());
                            }

                            AllMovieParticleRows[NameMapping[ColumnMicName[r]]].Add(TableBoxNetAll.GetRow(r));
                        }
                    }
                    else
                    {
                        TableBoxNetAll = new Star(new string[] { });
                    }

                    #region Make sure all columns are there

                    if (!TableBoxNetAll.HasColumn("rlnCoordinateX"))
                        TableBoxNetAll.AddColumn("rlnCoordinateX", "0.0");

                    if (!TableBoxNetAll.HasColumn("rlnCoordinateY"))
                        TableBoxNetAll.AddColumn("rlnCoordinateY", "0.0");

                    if (!TableBoxNetAll.HasColumn("rlnMagnification"))
                        TableBoxNetAll.AddColumn("rlnMagnification", "10000.0");
                    else
                        TableBoxNetAll.SetColumn("rlnMagnification", Helper.ArrayOfConstant("10000.0", TableBoxNetAll.RowCount));

                    if (!TableBoxNetAll.HasColumn("rlnDetectorPixelSize"))
                        TableBoxNetAll.AddColumn("rlnDetectorPixelSize", Options.BinnedPixelSizeMean.ToString("F5", CultureInfo.InvariantCulture));
                    else
                        TableBoxNetAll.SetColumn("rlnDetectorPixelSize", Helper.ArrayOfConstant(Options.BinnedPixelSizeMean.ToString("F5", CultureInfo.InvariantCulture), TableBoxNetAll.RowCount));

                    if (!TableBoxNetAll.HasColumn("rlnVoltage"))
                        TableBoxNetAll.AddColumn("rlnVoltage", "300.0");

                    if (!TableBoxNetAll.HasColumn("rlnSphericalAberration"))
                        TableBoxNetAll.AddColumn("rlnSphericalAberration", "2.7");

                    if (!TableBoxNetAll.HasColumn("rlnAmplitudeContrast"))
                        TableBoxNetAll.AddColumn("rlnAmplitudeContrast", "0.07");

                    if (!TableBoxNetAll.HasColumn("rlnPhaseShift"))
                        TableBoxNetAll.AddColumn("rlnPhaseShift", "0.0");

                    if (!TableBoxNetAll.HasColumn("rlnDefocusU"))
                        TableBoxNetAll.AddColumn("rlnDefocusU", "0.0");

                    if (!TableBoxNetAll.HasColumn("rlnDefocusV"))
                        TableBoxNetAll.AddColumn("rlnDefocusV", "0.0");

                    if (!TableBoxNetAll.HasColumn("rlnDefocusAngle"))
                        TableBoxNetAll.AddColumn("rlnDefocusAngle", "0.0");

                    if (!TableBoxNetAll.HasColumn("rlnCtfMaxResolution"))
                        TableBoxNetAll.AddColumn("rlnCtfMaxResolution", "999.0");

                    if (!TableBoxNetAll.HasColumn("rlnImageName"))
                        TableBoxNetAll.AddColumn("rlnImageName", "None");

                    if (!TableBoxNetAll.HasColumn("rlnMicrographName"))
                        TableBoxNetAll.AddColumn("rlnMicrographName", "None");

                    #endregion

                    #region Repair

                    var RepairMovies = TempMovies.Where(m => !AllMovieParticleRows.ContainsKey(m) && m.OptionsBoxNet != null && File.Exists(m.MatchingDir + m.RootName + "_" + BoxNetSuffix + ".star")).ToList();
                    if (RepairMovies.Count() > 0)
                    {

                        int NRepaired = 0;
                        foreach (var item in RepairMovies)
                        {
                            float2[] Positions = Star.LoadFloat2(item.MatchingDir + item.RootName + "_" + BoxNetSuffix + ".star",
                                                                    "rlnCoordinateX",
                                                                    "rlnCoordinateY");

                            float[] Defoci = new float[Positions.Length];
                            if (item.GridCTFDefocus != null)
                                Defoci = item.GridCTFDefocus.GetInterpolated(Positions.Select(v => new float3(v.X / (item.OptionsBoxNet.Dimensions.X / (float)item.OptionsBoxNet.BinnedPixelSizeMean),
                                                                                                        v.Y / (item.OptionsBoxNet.Dimensions.Y / (float)item.OptionsBoxNet.BinnedPixelSizeMean),
                                                                                                        0.5f)).ToArray());
                            float Astigmatism = (float)item.CTF.DefocusDelta / 2;
                            float PhaseShift = item.GridCTFPhase.GetInterpolated(new float3(0.5f)) * 180;

                            List<List<string>> NewRows = new List<List<string>>();
                            for (int r = 0; r < Positions.Length; r++)
                            {
                                string[] Row = Helper.ArrayOfConstant("0", TableBoxNetAll.ColumnCount);

                                Row[TableBoxNetAll.GetColumnID("rlnMagnification")] = "10000.0";
                                Row[TableBoxNetAll.GetColumnID("rlnDetectorPixelSize")] = item.OptionsBoxNet.BinnedPixelSizeMean.ToString("F5", CultureInfo.InvariantCulture);

                                Row[TableBoxNetAll.GetColumnID("rlnDefocusU")] = ((Defoci[r] + Astigmatism) * 1e4f).ToString("F1", CultureInfo.InvariantCulture);
                                Row[TableBoxNetAll.GetColumnID("rlnDefocusV")] = ((Defoci[r] - Astigmatism) * 1e4f).ToString("F1", CultureInfo.InvariantCulture);
                                Row[TableBoxNetAll.GetColumnID("rlnDefocusAngle")] = item.CTF.DefocusAngle.ToString("F1", CultureInfo.InvariantCulture);
                                Row[TableBoxNetAll.GetColumnID("rlnVoltage")] = item.CTF.Voltage.ToString("F1", CultureInfo.InvariantCulture);
                                Row[TableBoxNetAll.GetColumnID("rlnSphericalAberration")] = item.CTF.Cs.ToString("F4", CultureInfo.InvariantCulture);
                                Row[TableBoxNetAll.GetColumnID("rlnAmplitudeContrast")] = item.CTF.Amplitude.ToString("F3", CultureInfo.InvariantCulture);
                                Row[TableBoxNetAll.GetColumnID("rlnPhaseShift")] = PhaseShift.ToString("F1", CultureInfo.InvariantCulture);
                                Row[TableBoxNetAll.GetColumnID("rlnCtfMaxResolution")] = item.CTFResolutionEstimate.ToString("F1", CultureInfo.InvariantCulture);
                                Row[TableBoxNetAll.GetColumnID("rlnCoordinateX")] = Positions[r].X.ToString("F2", CultureInfo.InvariantCulture);
                                Row[TableBoxNetAll.GetColumnID("rlnCoordinateY")] = Positions[r].Y.ToString("F2", CultureInfo.InvariantCulture);
                                Row[TableBoxNetAll.GetColumnID("rlnImageName")] = (r + 1).ToString("D7") + "@particles/" + item.RootName + "_" + BoxNetSuffix + ".mrcs";
                                Row[TableBoxNetAll.GetColumnID("rlnMicrographName")] = item.Name;

                                NewRows.Add(Row.ToList());
                            }

                            AllMovieParticleRows.Add(item, NewRows);

                            NRepaired++;
                        }
                    }

                    #endregion
                }

                #endregion

                Console.WriteLine("Section 4 : spawn workers.");
                #region Spawn workers and let them load gain refs

                WorkerWrapper[] Workers = new WorkerWrapper[GPU.GetDeviceCount() * GlobalOptions.ProcessesPerDevice];
                foreach (var gpuID in UsedDeviceProcesses)
                {
                    Workers[gpuID] = new WorkerWrapper(gpuID);
                    Workers[gpuID].SetHeaderlessParams(new int2(Options.Import.HeaderlessWidth, Options.Import.HeaderlessHeight), 
                                                        Options.Import.HeaderlessOffset, 
                                                        Options.Import.HeaderlessType);

                    if ((!string.IsNullOrEmpty(Options.Import.GainPath) || !string.IsNullOrEmpty(Options.Import.DefectsPath)) && 
                        (Options.Import.CorrectGain || Options.Import.CorrectDefects))
                        Workers[gpuID].LoadGainRef(Options.Import.CorrectGain ? Options.Import.GainPath : "",
                                                    Options.Import.GainFlipX,
                                                    Options.Import.GainFlipY,
                                                    Options.Import.GainTranspose,
                                                    Options.Import.CorrectDefects ? Options.Import.DefectsPath : "");
                    else
                        Workers[gpuID].LoadGainRef("", false, false, false, "");
                }

                bool CheckedGainDims = ImageGain == null;

                #endregion
                Console.WriteLine("Section 5 : loops.");
                
                bool first = true;
                while (true)
                {
                    // Console.WriteLine("Infinity Loops");
                    if (!IsPreprocessing)
                        break;

                    #region Figure out what needs preprocessing

                    Movie[] ImmutableItems = FileDiscoverer.GetImmutableFiles();

                    if(first){
                        Console.WriteLine("ImmutableItems length: " + ImmutableItems.Length);
                        first = false;
                    }
                    
                    List<Movie> NeedProcessing = new List<Movie>();

                    ProcessingOptionsMovieCTF OptionsCTF = Options.GetProcessingMovieCTF();
                    ProcessingOptionsMovieMovement OptionsMovement = Options.GetProcessingMovieMovement();
                    ProcessingOptionsMovieExport OptionsExport = Options.GetProcessingMovieExport();
                    ProcessingOptionsBoxNet OptionsBoxNet = Options.GetProcessingBoxNet();

                    bool DoCTF = Options.ProcessCTF;
                    bool DoMovement = Options.ProcessMovement;
                    bool DoPicking = Options.ProcessPicking;

                    foreach (var item in ImmutableItems)
                    {
                        ProcessingStatus Status = GetMovieProcessingStatus(item, OptionsCTF, OptionsMovement, OptionsBoxNet, OptionsExport, Options, false);

                        if (Status == ProcessingStatus.Outdated || Status == ProcessingStatus.Unprocessed){
                            NeedProcessing.Add(item);
                            //Console.WriteLine("Added to NeedProcessing : " + item);
                        }else{
                            //Console.WriteLine("Not added : " + Status);
                        }
                    }

                    #endregion

                    if (NeedProcessing.Count == 0)
                    {
                        await Task.Delay(20);
                        continue;
                    }

                    #region Make sure gain dims match those of first image to be processed

                    if (!CheckedGainDims)
                    {
                        string ItemPath;

                        if (NeedProcessing[0].GetType() == typeof(Movie))
                            ItemPath = NeedProcessing[0].Path;
                        else
                            ItemPath = Path.Combine(((TiltSeries)NeedProcessing[0]).DirectoryName, ((TiltSeries)NeedProcessing[0]).TiltMoviePaths[0]);

                        MapHeader Header = MapHeader.ReadFromFilePatient(50, 500,
                                                                            ItemPath,
                                                                            new int2(Options.Import.HeaderlessWidth, Options.Import.HeaderlessHeight),
                                                                            Options.Import.HeaderlessOffset,
                                                                            ImageFormatsHelper.StringToType(Options.Import.HeaderlessType));

                        if (Helper.PathToExtension(ItemPath).ToLower() != ".eer")
                            if (Header.Dimensions.X != ImageGain.Dims.X || Header.Dimensions.Y != ImageGain.Dims.Y)
                            {
                                ImageGain.Dispose();
                                DefectMap?.Dispose();

                                foreach (var worker in Workers)
                                    worker?.Dispose();

                                Console.WriteLine("Oopsie", "Image dimensions do not match those of the gain reference. Maybe it needs to be rotated or transposed?");

                                break;
                            }

                        CheckedGainDims = true;
                    }

                    #endregion

                    #region Perform preprocessing on all available GPUs

                    Helper.ForEachGPU(NeedProcessing, (item, gpuID) =>
                    {
                        if (!IsPreprocessing)
                            return true;    // This cancels the iterator

                        Image OriginalStack = null;

                        try
                        {
                            var TimerOverall = BenchmarkAllProcessing.Start();

                            ProcessingOptionsMovieCTF CurrentOptionsCTF = Options.GetProcessingMovieCTF();
                            ProcessingOptionsMovieMovement CurrentOptionsMovement = Options.GetProcessingMovieMovement();
                            ProcessingOptionsBoxNet CurrentOptionsBoxNet = Options.GetProcessingBoxNet();
                            ProcessingOptionsMovieExport CurrentOptionsExport = Options.GetProcessingMovieExport();

                            bool DoExport = OptionsExport.DoAverage || OptionsExport.DoStack || OptionsExport.DoDeconv || (DoPicking && !File.Exists(item.AveragePath));

                            bool NeedsNewCTF = CurrentOptionsCTF != item.OptionsCTF && DoCTF;
                            bool NeedsNewMotion = CurrentOptionsMovement != item.OptionsMovement && DoMovement;
                            bool NeedsNewPicking = DoPicking &&
                                                    (CurrentOptionsBoxNet != item.OptionsBoxNet ||
                                                    NeedsNewMotion);
                            bool NeedsNewExport = DoExport &&
                                                    (NeedsNewMotion ||
                                                    CurrentOptionsExport != item.OptionsMovieExport ||
                                                    (CurrentOptionsExport.DoDeconv && NeedsNewCTF));

                            bool NeedsMoreDenoisingExamples = !Directory.Exists(item.DenoiseTrainingDirOdd) || 
                                                                Directory.EnumerateFiles(item.DenoiseTrainingDirOdd, "*.mrc").Count() < 128;   // Having more than 128 examples is a waste of space
                            bool DoesDenoisingExampleExist = File.Exists(item.DenoiseTrainingOddPath);
                            bool NeedsDenoisingExample = NeedsMoreDenoisingExamples || (DoesDenoisingExampleExist && (NeedsNewCTF || NeedsNewExport));
                            CurrentOptionsExport.DoDenoise = NeedsDenoisingExample;

                            MapHeader OriginalHeader = null;
                            decimal ScaleFactor = 1M / (decimal)Math.Pow(2, (double)Options.Import.BinTimes);

                            bool NeedStack = NeedsNewCTF ||
                                                NeedsNewMotion ||
                                                NeedsNewExport ||
                                                (NeedsNewPicking && CurrentOptionsBoxNet.ExportParticles);

                            if (!IsTomo)
                            {
                                Console.WriteLine(GPU.GetDevice() + " loading...");
                                var TimerRead = BenchmarkRead.Start();

                                LoadAndPrepareHeaderAndMap(item.Path, ImageGain, DefectMap, ScaleFactor, out OriginalHeader, out OriginalStack, false);
                                if (NeedStack)
                                    Workers[gpuID].LoadStack(item.Path, ScaleFactor, CurrentOptionsExport.EERGroupFrames);

                                BenchmarkRead.Finish(TimerRead);
                                Console.WriteLine(GPU.GetDevice() + " loaded.");
                            }

                            // Store original dimensions in Angstrom
                            if (!IsTomo)
                            {
                                CurrentOptionsCTF.Dimensions = OriginalHeader.Dimensions.MultXY((float)Options.PixelSizeMean);
                                CurrentOptionsMovement.Dimensions = OriginalHeader.Dimensions.MultXY((float)Options.PixelSizeMean);
                                CurrentOptionsBoxNet.Dimensions = OriginalHeader.Dimensions.MultXY((float)Options.PixelSizeMean);
                                CurrentOptionsExport.Dimensions = OriginalHeader.Dimensions.MultXY((float)Options.PixelSizeMean);
                            }
                            else
                            {
                                ((TiltSeries)item).LoadMovieSizes(CurrentOptionsCTF);

                                float3 StackDims = new float3(((TiltSeries)item).ImageDimensionsPhysical, ((TiltSeries)item).NTilts);
                                CurrentOptionsCTF.Dimensions = StackDims;
                                CurrentOptionsMovement.Dimensions = StackDims;
                                CurrentOptionsExport.Dimensions = StackDims;
                            }
                            
                            Console.WriteLine(GPU.GetDevice() + " processing...");

                            if (!IsPreprocessing)
                            {
                                OriginalStack?.Dispose();
                                return true;
                            } // These checks are needed to abort the processing faster

                            if (DoCTF && NeedsNewCTF)
                            {
                                var TimerCTF = BenchmarkCTF.Start();

                                if (!IsTomo)
                                {
                                    Workers[gpuID].MovieProcessCTF(item.Path, CurrentOptionsCTF);
                                    item.LoadMeta();
                                }
                                else
                                {
                                    Workers[gpuID].TomoProcessCTF(item.Path, CurrentOptionsCTF);
                                    item.LoadMeta();
                                }

                                BenchmarkCTF.Finish(TimerCTF);
                                GlobalOptions.LogProcessingCTF(CurrentOptionsCTF, item.CTF, (float)item.CTFResolutionEstimate);
                            }
                            if (!IsPreprocessing)
                            {
                                OriginalStack?.Dispose();
                                return true;
                            }

                            if (DoMovement && NeedsNewMotion && !IsTomo)
                            {
                                var TimerMotion = BenchmarkMotion.Start();

                                Workers[gpuID].MovieProcessMovement(item.Path, CurrentOptionsMovement);
                                item.LoadMeta();
                                //item.ProcessShift(OriginalStack, CurrentOptionsMovement);

                                BenchmarkMotion.Finish(TimerMotion);
                                GlobalOptions.LogProcessingMovement(CurrentOptionsMovement, (float)item.MeanFrameMovement);
                            }
                            if (!IsPreprocessing)
                            {
                                OriginalStack?.Dispose();
                                return true;
                            }

                            if (DoExport && NeedsNewExport && !IsTomo)
                            {
                                var TimerOutput = BenchmarkOutput.Start();

                                Workers[gpuID].MovieExportMovie(item.Path, CurrentOptionsExport);
                                item.LoadMeta();
                                //item.ExportMovie(OriginalStack, CurrentOptionsExport);

                                BenchmarkOutput.Finish(TimerOutput);
                            }

                            if (!File.Exists(item.ThumbnailsPath))
                                item.CreateThumbnail(384, 2.5f);

                            if (DoPicking && NeedsNewPicking && !IsTomo)
                            {
                                var TimerPicking = BenchmarkPicking.Start();

                                Image AverageForPicking = Image.FromFilePatient(50, 500, item.AveragePath);

                                // Let only one process per GPU access BoxNet on that GPU, otherwise TF memory consumption can explode
                                lock (BoxNetLocks[gpuID % NDevices])
                                    item.MatchBoxNet2(new[] { BoxNetworks[gpuID % NDevices] }, AverageForPicking, CurrentOptionsBoxNet, null);

                                GlobalOptions.LogProcessingBoxNet(CurrentOptionsBoxNet, item.GetParticleCount("_" + BoxNetSuffix));

                                #region Export particles if needed

                                if (CurrentOptionsBoxNet.ExportParticles)
                                {
                                    float2[] Positions = Star.LoadFloat2(item.MatchingDir + item.RootName + "_" + BoxNetSuffix + ".star",
                                                                            "rlnCoordinateX",
                                                                            "rlnCoordinateY").Select(v => v * AverageForPicking.PixelSize).ToArray();

                                    ProcessingOptionsParticlesExport ParticleOptions = new ProcessingOptionsParticlesExport
                                    {
                                        Suffix = "_" + BoxNetSuffix,

                                        BoxSize = CurrentOptionsBoxNet.ExportBoxSize,
                                        Diameter = (int)CurrentOptionsBoxNet.ExpectedDiameter,
                                        Invert = CurrentOptionsBoxNet.ExportInvert,
                                        Normalize = CurrentOptionsBoxNet.ExportNormalize,
                                        CorrectAnisotropy = true,

                                        PixelSizeX = CurrentOptionsBoxNet.PixelSizeX,
                                        PixelSizeY = CurrentOptionsBoxNet.PixelSizeY,
                                        PixelSizeAngle = CurrentOptionsBoxNet.PixelSizeAngle,
                                        Dimensions = CurrentOptionsBoxNet.Dimensions,

                                        BinTimes = CurrentOptionsBoxNet.BinTimes,
                                        GainPath = CurrentOptionsBoxNet.GainPath,
                                        DosePerAngstromFrame = Options.Import.DosePerAngstromFrame,

                                        DoAverage = true,
                                        DoStack = false,
                                        StackGroupSize = 1,
                                        SkipFirstN = Options.Export.SkipFirstN,
                                        SkipLastN = Options.Export.SkipLastN,

                                        Voltage = Options.CTF.Voltage
                                    };

                                    if (Positions.Length > 0)
                                    {
                                        Workers[gpuID].MovieExportParticles(item.Path, ParticleOptions, Positions);
                                        item.LoadMeta();
                                        //item.ExportParticles(OriginalStack, Positions, ParticleOptions);
                                    }

                                    OriginalStack?.Dispose();
                                    Console.WriteLine(GPU.GetDevice() + " processed.");

                                    float[] Defoci = new float[Positions.Length];
                                    if (item.GridCTFDefocus != null)
                                        Defoci = item.GridCTFDefocus.GetInterpolated(Positions.Select(v => new float3(v.X / CurrentOptionsBoxNet.Dimensions.X,
                                                                                                                v.Y / CurrentOptionsBoxNet.Dimensions.Y,
                                                                                                                0.5f)).ToArray());
                                    float Astigmatism = (float)item.CTF.DefocusDelta / 2;
                                    float PhaseShift = item.GridCTFPhase.GetInterpolated(new float3(0.5f)) * 180;

                                    List<List<string>> NewRows = new List<List<string>>();
                                    for (int r = 0; r < Positions.Length; r++)
                                    {
                                        string[] Row = Helper.ArrayOfConstant("0", TableBoxNetAll.ColumnCount);

                                        Row[TableBoxNetAll.GetColumnID("rlnMagnification")] = "10000.0";
                                        Row[TableBoxNetAll.GetColumnID("rlnDetectorPixelSize")] = Options.BinnedPixelSizeMean.ToString("F5", CultureInfo.InvariantCulture);

                                        Row[TableBoxNetAll.GetColumnID("rlnDefocusU")] = ((Defoci[r] + Astigmatism) * 1e4f).ToString("F1", CultureInfo.InvariantCulture);
                                        Row[TableBoxNetAll.GetColumnID("rlnDefocusV")] = ((Defoci[r] - Astigmatism) * 1e4f).ToString("F1", CultureInfo.InvariantCulture);
                                        Row[TableBoxNetAll.GetColumnID("rlnDefocusAngle")] = item.CTF.DefocusAngle.ToString("F1", CultureInfo.InvariantCulture);
                                        Row[TableBoxNetAll.GetColumnID("rlnVoltage")] = item.CTF.Voltage.ToString("F1", CultureInfo.InvariantCulture);
                                        Row[TableBoxNetAll.GetColumnID("rlnSphericalAberration")] = item.CTF.Cs.ToString("F4", CultureInfo.InvariantCulture);
                                        Row[TableBoxNetAll.GetColumnID("rlnAmplitudeContrast")] = item.CTF.Amplitude.ToString("F3", CultureInfo.InvariantCulture);
                                        Row[TableBoxNetAll.GetColumnID("rlnPhaseShift")] = PhaseShift.ToString("F1", CultureInfo.InvariantCulture);
                                        Row[TableBoxNetAll.GetColumnID("rlnCtfMaxResolution")] = item.CTFResolutionEstimate.ToString("F1", CultureInfo.InvariantCulture);
                                        Row[TableBoxNetAll.GetColumnID("rlnCoordinateX")] = (Positions[r].X / (float)CurrentOptionsBoxNet.BinnedPixelSizeMean).ToString("F2", CultureInfo.InvariantCulture);
                                        Row[TableBoxNetAll.GetColumnID("rlnCoordinateY")] = (Positions[r].Y / (float)CurrentOptionsBoxNet.BinnedPixelSizeMean).ToString("F2", CultureInfo.InvariantCulture);
                                        Row[TableBoxNetAll.GetColumnID("rlnImageName")] = (r + 1).ToString("D7") + "@particles/" + item.RootName + "_" + BoxNetSuffix + ".mrcs";
                                        Row[TableBoxNetAll.GetColumnID("rlnMicrographName")] = item.Name;

                                        NewRows.Add(Row.ToList());
                                    }

                                    List<List<string>> RowsAll = new List<List<string>>();
                                    List<List<string>> RowsGood = new List<List<string>>();

                                    lock (AllMovieParticleRows)
                                    {
                                        if (!AllMovieParticleRows.ContainsKey(item))
                                            AllMovieParticleRows.Add(item, NewRows);
                                        else
                                            AllMovieParticleRows[item] = NewRows;

                                        foreach (var pair in AllMovieParticleRows)
                                        {
                                            RowsAll.AddRange(pair.Value);
                                            if (!(pair.Key.UnselectFilter || (pair.Key.UnselectManual != null && pair.Key.UnselectManual.Value)))
                                                RowsGood.AddRange(pair.Value);
                                        }
                                    }

                                    if (TableBoxNetConcurrent == 0)
                                    {
                                        lock (TableBoxNetAllWriteLock)
                                            TableBoxNetConcurrent++;

                                        Task.Run(() =>
                                        {
                                            Star TempTableAll = new Star(TableBoxNetAll.GetColumnNames());
                                            TempTableAll.AddRow(RowsAll);

                                            bool SuccessAll = false;
                                            while (!SuccessAll)
                                            {
                                                try
                                                {
                                                    TempTableAll.Save(PathBoxNetAll + "_" + item.RootName);
                                                    lock (TableBoxNetAllWriteLock)
                                                    {
                                                        if (File.Exists(PathBoxNetAll))
                                                            File.Delete(PathBoxNetAll);
                                                        File.Move(PathBoxNetAll + "_" + item.RootName, PathBoxNetAll);

                                                        if (Options.Picking.DoRunningWindow && TempTableAll.RowCount > 0)
                                                        {
                                                            TempTableAll.CreateSubset(Helper.ArrayOfSequence(Math.Max(0, TempTableAll.RowCount - Options.Picking.RunningWindowLength), 
                                                                                                                TempTableAll.RowCount - 1, 
                                                                                                                1)).Save(PathBoxNetAllSubset);
                                                        }
                                                    }
                                                    SuccessAll = true;
                                                }
                                                catch { }
                                            }

                                            Star TempTableGood = new Star(TableBoxNetAll.GetColumnNames());
                                            TempTableGood.AddRow(RowsGood);

                                            bool SuccessGood = false;
                                            while (!SuccessGood)
                                            {
                                                try
                                                {
                                                    TempTableGood.Save(PathBoxNetFiltered + "_" + item.RootName);
                                                    lock (TableBoxNetAllWriteLock)
                                                    {
                                                        if (File.Exists(PathBoxNetFiltered))
                                                            File.Delete(PathBoxNetFiltered);
                                                        File.Move(PathBoxNetFiltered + "_" + item.RootName, PathBoxNetFiltered);

                                                        if (Options.Picking.DoRunningWindow && TempTableGood.RowCount > 0)
                                                        {
                                                            TempTableGood.CreateSubset(Helper.ArrayOfSequence(Math.Max(0, TempTableGood.RowCount - Options.Picking.RunningWindowLength),
                                                                                                                TempTableGood.RowCount - 1,
                                                                                                                1)).Save(PathBoxNetFilteredSubset);
                                                        }
                                                    }
                                                    SuccessGood = true;
                                                }
                                                catch { }
                                            }

                                            lock (TableBoxNetAllWriteLock)
                                                TableBoxNetConcurrent--;
                                        });
                                    }
                                }
                                else
                                {
                                    OriginalStack?.Dispose();
                                    Console.WriteLine(GPU.GetDevice() + " processed.");
                                }

                                #endregion

                                AverageForPicking.Dispose();

                                BenchmarkPicking.Finish(TimerPicking);
                            }
                            else
                            {
                                OriginalStack?.Dispose();
                                Console.WriteLine(GPU.GetDevice() + " processed.");
                            }

                            BenchmarkAllProcessing.Finish(TimerOverall);

                            UpdateStatsAll();

                            return false; // No need to cancel GPU ForEach iterator
                        }
                        catch (Exception exc)
                        {
                            OriginalStack?.Dispose();

                            item.UnselectManual = true;
                            UpdateStatsAll();

                            return false;
                        }
                    }, 1, UsedDeviceProcesses);

                    UpdateStatsAll(); // Is it okay to just remove dispatcher? 2022/08/16 VKJY
                    #endregion
                }
                Console.WriteLine("Section 6 : loop ended.");
                ImageGain?.Dispose();
                DefectMap?.Dispose();

                foreach (var worker in Workers)
                    worker?.Dispose();

                foreach (int d in UsedDevices)
                    BoxNetworks[d]?.Dispose();

                Console.WriteLine("Section 7 : make sure all particle tables.");
                #region Make sure all particle tables are written out in their most recent form

                if (Options.ProcessPicking && Options.Picking.DoExport && !string.IsNullOrEmpty(Options.Picking.ModelPath))
                {
                    // await Dispatcher.Invoke(async () => ProgressDialog = await this.ShowProgressAsync($"Waiting for the last particle files to be written out...", ""));
                    
                    List<List<string>> RowsAll = new List<List<string>>();
                    List<List<string>> RowsGood = new List<List<string>>();

                    lock (AllMovieParticleRows)
                    {
                        foreach (var pair in AllMovieParticleRows)
                        {
                            RowsAll.AddRange(pair.Value);
                            if (!(pair.Key.UnselectFilter || (pair.Key.UnselectManual != null && pair.Key.UnselectManual.Value)))
                                RowsGood.AddRange(pair.Value);
                        }
                    }

                    while (TableBoxNetConcurrent > 0)
                        Thread.Sleep(50);
                    
                    Star TempTableAll = new Star(TableBoxNetAll.GetColumnNames());
                    TempTableAll.AddRow(RowsAll);

                    bool SuccessAll = false;
                    while (!SuccessAll)
                    {
                        try
                        {
                            TempTableAll.Save(PathBoxNetAll + "_temp");
                            lock (TableBoxNetAllWriteLock)
                            {
                                if (File.Exists(PathBoxNetAll))
                                    File.Delete(PathBoxNetAll);
                                File.Move(PathBoxNetAll + "_temp", PathBoxNetAll);
                            }
                            SuccessAll = true;
                        }
                        catch { }
                    }

                    Star TempTableGood = new Star(TableBoxNetAll.GetColumnNames());
                    TempTableGood.AddRow(RowsGood);

                    bool SuccessGood = false;
                    while (!SuccessGood)
                    {
                        try
                        {
                            TempTableGood.Save(PathBoxNetFiltered + "_temp");
                            lock (TableBoxNetAllWriteLock)
                            {
                                if (File.Exists(PathBoxNetFiltered))
                                    File.Delete(PathBoxNetFiltered);
                                File.Move(PathBoxNetFiltered + "_temp", PathBoxNetFiltered);
                            }
                            SuccessGood = true;
                        }
                        catch { }
                    }

                }

                #endregion
                // });
            }
            else
            {
                // Stop!
                IsStoppingPreprocessing = true;

                IsPreprocessing = false;
                if (PreprocessingTask != null)
                {
                    await PreprocessingTask;
                    PreprocessingTask = null;
                }
                
                #region Timers

                BenchmarkAllProcessing.Clear();
                BenchmarkRead.Clear();
                BenchmarkCTF.Clear();
                BenchmarkMotion.Clear();
                BenchmarkPicking.Clear();
                BenchmarkOutput.Clear();

                #endregion

                UpdateStatsAll();

                IsStoppingPreprocessing = false;
            }
            Console.WriteLine("End of the onclick listener");
            await Task.Delay(500);
        }

        private async Task ManuallyDeselect()
        {            
            Movie[] ImmutableItems = FileDiscoverer.GetImmutableFiles();

            Console.WriteLine("ImmutableItems length: " + ImmutableItems.Length);
            
            while(true){
                Console.WriteLine("Type the name of the mrc file to be deselected(empty input to quit)");
                Console.WriteLine("ex) TS_01_039_-60.0.mrc");
                Console.Write("input>");

                String deselectName = Console.ReadLine();
                
                if(String.Equals(deselectName, ""))
                    break;
                
                foreach(var item in ImmutableItems){
                    if(String.Equals(item.Name, deselectName)){
                        item.LoadMeta();
                        item.UnselectManual = true;
                        item.SaveMeta();
                        Console.WriteLine($"{item.Name} was deselected!");
                    }
                }
            }
        }
        
        private async Task IMODStackGeneration()
        {
            DialogTomoImportImod stackGenImod = new DialogTomoImportImod(Options);
            stackGenImod.ButtonMdocPath_Click("/cdata/EMPIAR-10164_ORIGINAL/mdoc/");
            stackGenImod.ButtonMoviePath_Click("/cdata/EMPIAR-10164_ORIGINAL/frames/");
            await stackGenImod.Reevaluate();
            await stackGenImod.ButtonCreateStacks_Click();
        }
        
        private async Task CTFEstimation(){
            // Import
            DialogTomoImportImod stackGenImod = new DialogTomoImportImod(Options);
            stackGenImod.ButtonMdocPath_Click("/cdata/EMPIAR-10164_ORIGINAL/mdoc/");
            stackGenImod.ButtonMoviePath_Click("/cdata/EMPIAR-10164_ORIGINAL/frames/");
            stackGenImod.ButtonImodPath_Click("/cdata/EMPIAR-10164_ORIGINAL/frames/dynamo_alignments");
            stackGenImod.SetValue(false, 1.3500m, 3.00m);
            await stackGenImod.Reevaluate();
            await stackGenImod.ButtonWrite_Click();
            // CTF estimation
            await fileDiscovererReadyTOMOSTAR();
            Movie[] TempMovies = FileDiscoverer.GetImmutableFiles();
            Console.WriteLine($"-{TempMovies.Length} Movie was found.");
            if(TempMovies.Length >= 1){
                Console.WriteLine($"-First item is {TempMovies[0].Path}");
            }
            await ButtonProcessOneItemCTF_OnClick(TempMovies[0]); // Modify if we want to deal with multiple CTF
            // Check Handedness

        }

        private async Task Reconstruction(){
            await fileDiscovererReadyTOMOSTAR();
            TiltSeries[] TiltSeries = FileDiscoverer.GetImmutableFiles().Cast<TiltSeries>().ToArray();
            Console.WriteLine($"-{TiltSeries.Length} Movie was found.");
            Console.WriteLine("-Extension was changed to '*.tomostar'.");

            DialogTomoReconstruction fulltomoReconst = new DialogTomoReconstruction(TiltSeries, Options);
            await fulltomoReconst.ButtonReconstruct_OnClick(true, false);
            Console.WriteLine("RECONSTRUCTION Done.");
        }
        public Program(){
            #region Make sure everything is OK with GPUs
            System.Int32 gpuDeviceCountV = 0; // Options.Runtime.DeviceCount??? ??????.
            try
            {
                // Options.Runtime.DeviceCount = GPU.GetDeviceCount();
                Options.Runtime.DeviceCount = 1;
                if (Options.Runtime.DeviceCount <= 0){
                    Console.WriteLine("No GPU detected!");
                    throw new Exception();
                }
            }
            catch (Exception exc){
                Console.WriteLine("GPU settings exception! message : {0}", exc.Message);
                return;
            }
            Console.WriteLine("Detected GPU devices number : {0}", gpuDeviceCountV);
            GPU.SetDevice(0);

            #endregion

            // CUSTOM VKJY
            Console.WriteLine("Enter the path of the *star file [ Should not include the trailing '/' ]");
            //starPath = Console.ReadLine();
            starPath = direcNameV + "EMPIAR-10164_ORIGINAL/frames/";
            Console.WriteLine("Your input is... {0}", starPath);

            #region File discoverer

            FileDiscoverer = new FileDiscoverer();
            fileDiscovererReadyMRC();
            FileDiscoverer.FilesChanged += FileDiscoverer_FilesChanged;
            FileDiscoverer.IncubationStarted += FileDiscoverer_IncubationStarted;
            FileDiscoverer.IncubationEnded += FileDiscoverer_IncubationEnded;
            
            #endregion
        }

        ~Program(){
            FileDiscoverer.Shutdown();
        }

        async static Task Main(string[] args){
            Console.WriteLine("-------------- Program for Warp in Linux  --------------");
            Options.Load(direcNameV + "test.settings");
            Console.WriteLine("Import Result. Import directory : " + Options.Import.Folder); 

            Program main = new Program();

            Console.WriteLine("----1 : Preprocessing");
            Console.WriteLine("----2 : Manually deselect");
            Console.WriteLine("----3 : IMOD stack generation");
            Console.WriteLine("----4 : Dynamo_alignment results import, CTF estimation");
            Console.WriteLine("----5 : Check Handedness");
            Console.WriteLine("----6 : Reconstruction");
            Console.WriteLine("Write the number!");
            int whichTask = Convert.ToInt32(Console.ReadLine());
            switch(whichTask){
                case 1:
                    Console.WriteLine("1 : Preprocessing!");
                    await main.Preprocessing();
                    break;
                case 2:
                    // After Preprocessing, with Thumbnail, we can have a list to eliminate(manually)
                    // 2 : Deselect Bad images.
                    Console.WriteLine("2 : Manually deselect.");
                    await main.ManuallyDeselect();
                    break;
                case 3:
                    // IMOD stack generation!
                    Console.WriteLine("3 : IMOD stack generation!");
                    Program.Options.Import.Extension = "*.tomostar";
                    Console.WriteLine("-Extension was changed to '*.tomostar'.");
                    await main.IMODStackGeneration();
                    break;
                case 4:
                    Console.WriteLine("4 : Import and CTF!");
                    Program.Options.Import.Extension = "*.tomostar";
                    Console.WriteLine("-Extension was changed to '*.tomostar'.");
                    await main.CTFEstimation();
                    break;
                case 5:
                    Console.WriteLine("5 : Check Handedness!");
                    Program.Options.Import.Extension = "*.tomostar";
                    await main.fileDiscovererReadyTOMOSTAR();
                    Movie[] TempMovies = main.FileDiscoverer.GetImmutableFiles();
                    Console.WriteLine($"-{TempMovies.Length} Movie was found.");
                    await main.ButtonProcessOneItemTiltHandedness_Click(TempMovies[0]);
                    break;
                case 6:
                    Console.WriteLine("6 : Reconstruction!");
                    Program.Options.Import.Extension = "*.tomostar";
                    await main.Reconstruction();
                    break;
                default:
                    Console.WriteLine("N : No input!");
                    break;
            }
            return;
        }

        #region Update region 
        public void UpdateStatsAll(){
            UpdateFilterRanges();
            UpdateFilterResult();
            UpdateStatsAstigmatismPlot();
            UpdateStatsStatus();
            UpdateFilterSuffixMenu();
            UpdateBenchmarkTimes();
        }

                private void UpdateStatsStatus()
        {
            Movie[] Items = FileDiscoverer.GetImmutableFiles();

            bool HaveCTF = Options.ProcessCTF || Items.Any(v => v.OptionsCTF != null && v.CTF != null);
            bool HavePhase = Options.CTF.DoPhase || Items.Any(v => v.OptionsCTF != null && v.OptionsCTF.DoPhase);
            bool HaveMovement = Options.ProcessMovement || Items.Any(v => v.OptionsMovement != null);
            bool HaveParticles = Items.Any(m => m.HasParticleSuffix(Options.Filter.ParticlesSuffix));

            ProcessingOptionsMovieCTF OptionsCTF = Options.GetProcessingMovieCTF();
            ProcessingOptionsMovieMovement OptionsMovement = Options.GetProcessingMovieMovement();
            ProcessingOptionsBoxNet OptionsBoxNet = Options.GetProcessingBoxNet();
            ProcessingOptionsMovieExport OptionsExport = Options.GetProcessingMovieExport();

            int[] ColorIDs = new int[Items.Length];
            int NProcessed = 0, NOutdated = 0, NUnprocessed = 0, NFilteredOut = 0, NUnselected = 0;
            for (int i = 0; i < Items.Length; i++)
            {
                ProcessingStatus Status = GetMovieProcessingStatus(Items[i], OptionsCTF, OptionsMovement, OptionsBoxNet, OptionsExport, Options);
                int ID = 0;
                switch (Status)
                {
                    case ProcessingStatus.Processed:
                        ID = 0;
                        NProcessed++;
                        break;
                    case ProcessingStatus.Outdated:
                        ID = 1;
                        NOutdated++;
                        break;
                    case ProcessingStatus.Unprocessed:
                        ID = 2;
                        NUnprocessed++;
                        break;
                    case ProcessingStatus.FilteredOut:
                        ID = 3;
                        NFilteredOut++;
                        break;
                    case ProcessingStatus.LeaveOut:
                        ID = 4;
                        NUnselected++;
                        break;
                }
                ColorIDs[i] = ID;
            }

            if (HaveCTF)
            {
                #region Defocus

                double[] DefocusValues = new double[Items.Length];
                for (int i = 0; i < Items.Length; i++)
                    if (Items[i].OptionsCTF != null && Items[i].CTF != null)
                        DefocusValues[i] = (double)Items[i].CTF.Defocus;
                    else
                        DefocusValues[i] = double.NaN;

                SingleAxisPoint[] DefocusPlotValues = new SingleAxisPoint[Items.Length];
                for (int i = 0; i < Items.Length; i++)
                    DefocusPlotValues[i] = new SingleAxisPoint(DefocusValues[i], ColorIDs[i], Items[i]);

                // Dispatcher.InvokeAsync(() => PlotStatsDefocus.Points = new ObservableCollection<SingleAxisPoint>(DefocusPlotValues));

                #endregion

                #region Phase

                if (HavePhase)
                {
                    double[] PhaseValues = new double[Items.Length];
                    for (int i = 0; i < Items.Length; i++)
                        if (Items[i].OptionsCTF != null && Items[i].CTF != null)
                            PhaseValues[i] = (double)Items[i].CTF.PhaseShift;
                        else
                            PhaseValues[i] = double.NaN;

                    SingleAxisPoint[] PhasePlotValues = new SingleAxisPoint[Items.Length];
                    for (int i = 0; i < Items.Length; i++)
                        PhasePlotValues[i] = new SingleAxisPoint(PhaseValues[i], ColorIDs[i], Items[i]);

                    // Dispatcher.InvokeAsync(() => PlotStatsPhase.Points = new ObservableCollection<SingleAxisPoint>(PhasePlotValues));
                }
                else{
                    Console.WriteLine("Line 1093.");
                    // Dispatcher.InvokeAsync(() => PlotStatsPhase.Points = null);
                }

                #endregion

                #region Resolution

                double[] ResolutionValues = new double[Items.Length];
                for (int i = 0; i < Items.Length; i++)
                    if (Items[i].CTFResolutionEstimate > 0)
                        ResolutionValues[i] = (double)Items[i].CTFResolutionEstimate;
                    else
                        ResolutionValues[i] = double.NaN;

                SingleAxisPoint[] ResolutionPlotValues = new SingleAxisPoint[Items.Length];
                for (int i = 0; i < Items.Length; i++)
                    ResolutionPlotValues[i] = new SingleAxisPoint(ResolutionValues[i], ColorIDs[i], Items[i]);

                //Dispatcher.InvokeAsync(() => PlotStatsResolution.Points = new ObservableCollection<SingleAxisPoint>(ResolutionPlotValues));

                #endregion
            }
            else
            {
                // Dispatcher.InvokeAsync(() =>
                // {
                //     //StatsSeriesAstigmatism0.Values = new ChartValues<ObservablePoint>();
                //     PlotStatsDefocus.Points = null;
                //     PlotStatsPhase.Points = null;
                //     PlotStatsResolution.Points = null;
                // });
            }

            if (HaveMovement)
            {
                double[] MovementValues = new double[Items.Length];
                for (int i = 0; i < Items.Length; i++)
                    if (Items[i].MeanFrameMovement > 0)
                        MovementValues[i] = (double)Items[i].MeanFrameMovement;
                    else
                        MovementValues[i] = double.NaN;

                SingleAxisPoint[] MovementPlotValues = new SingleAxisPoint[Items.Length];
                for (int i = 0; i < Items.Length; i++)
                    MovementPlotValues[i] = new SingleAxisPoint(MovementValues[i], ColorIDs[i], Items[i]);

                // Dispatcher.InvokeAsync(() => PlotStatsMotion.Points = new ObservableCollection<SingleAxisPoint>(MovementPlotValues));
            }
            else
            {
                // Dispatcher.InvokeAsync(() => PlotStatsMotion.Points = null);
            }

            if (HaveParticles)
            {
                int CountSum = 0, CountFilteredSum = 0;
                double[] ParticleValues = new double[Items.Length];
                for (int i = 0; i < Items.Length; i++)
                {
                    int Count = Items[i].GetParticleCount(Options.Filter.ParticlesSuffix);
                    if (Count >= 0)
                    {
                        ParticleValues[i] = Count;
                        CountSum += Count;

                        if (!(Items[i].UnselectFilter || (Items[i].UnselectManual != null && Items[i].UnselectManual.Value)))
                            CountFilteredSum += Count;
                    }
                    else
                        ParticleValues[i] = double.NaN;
                }

                SingleAxisPoint[] ParticlePlotValues = new SingleAxisPoint[Items.Length];
                for (int i = 0; i < Items.Length; i++)
                    ParticlePlotValues[i] = new SingleAxisPoint(ParticleValues[i], ColorIDs[i], Items[i]);

                // Dispatcher.InvokeAsync(() =>
                // {
                //     PlotStatsParticles.Points = new ObservableCollection<SingleAxisPoint>(ParticlePlotValues);
                //     TextStatsParticlesOverall.Value = CountSum.ToString();
                //     TextStatsParticlesFiltered.Value = CountFilteredSum.ToString();
                // });
            }
            else
            {
                // Dispatcher.InvokeAsync(() => PlotStatsParticles.Points = null);
            }

            {
                double[] MaskPercentageValues = new double[Items.Length];
                for (int i = 0; i < Items.Length; i++)
                    if (Items[i].MaskPercentage >= 0)
                        MaskPercentageValues[i] = (double)Items[i].MaskPercentage;
                    else
                        MaskPercentageValues[i] = double.NaN;

                SingleAxisPoint[] MaskPercentagePlotValues = new SingleAxisPoint[Items.Length];
                for (int i = 0; i < Items.Length; i++)
                    MaskPercentagePlotValues[i] = new SingleAxisPoint(MaskPercentageValues[i], ColorIDs[i], Items[i]);

                // Dispatcher.InvokeAsync(() => PlotStatsMaskPercentage.Points = new ObservableCollection<SingleAxisPoint>(MaskPercentagePlotValues));
            }
        }

        private void UpdateStatsAstigmatismPlot()
        {
            Movie[] Items = FileDiscoverer.GetImmutableFiles();

            bool HaveCTF = Options.ProcessCTF || Items.Any(v => v.OptionsCTF != null && v.CTF != null);

            if (HaveCTF)
            {
                #region Astigmatism

                DualAxisPoint[] AstigmatismPoints = new DualAxisPoint[Items.Length];
                for (int i = 0; i < Items.Length; i++)
                {
                    Movie item = Items[i];
                    DualAxisPoint P = new DualAxisPoint();
                    P.Context = item;
                    P.ColorID = i * 4 / Items.Length;
                    if (item.OptionsCTF != null && item.CTF != null)
                    {
                        P.X = Math.Round(Math.Cos((float)item.CTF.DefocusAngle * 2 * Helper.ToRad) * (float)item.CTF.DefocusDelta, 4);
                        P.Y = Math.Round(Math.Sin((float)item.CTF.DefocusAngle * 2 * Helper.ToRad) * (float)item.CTF.DefocusDelta, 4);
                        P.Label = item.CTF.DefocusDelta.ToString("F4");
                    }
                    else
                        P.Label = "";

                    AstigmatismPoints[i] = P;
                }

                // Dispatcher.InvokeAsync(() =>
                // {
                //     PlotStatsAstigmatism.Points = new ObservableCollection<DualAxisPoint>(AstigmatismPoints);
                // });

                #endregion
            }
            else
            {
                // Dispatcher.InvokeAsync(() =>
                // {
                //     PlotStatsAstigmatism.Points = new ObservableCollection<DualAxisPoint>();
                // });
            }
        }

        private void UpdateFilterRanges()
        {
            Movie[] Items = FileDiscoverer.GetImmutableFiles();
            Movie[] ItemsWithCTF = Items.Where(v => v.OptionsCTF != null && v.CTF != null).ToArray();
            Movie[] ItemsWithMovement = Items.Where(v => v.OptionsMovement != null).ToArray();

            #region Astigmatism (includes adjusting the plot elements)

            float2 AstigmatismMean = new float2();
            float AstigmatismStd = 0.1f;
            float AstigmatismMax = 0.4f;

            // Get all items with valid CTF information
            List<float2> AstigmatismPoints = new List<float2>(ItemsWithCTF.Length);
            foreach (var item in ItemsWithCTF)
                AstigmatismPoints.Add(new float2((float)Math.Cos((float)item.CTF.DefocusAngle * 2 * Helper.ToRad) * (float)item.CTF.DefocusDelta,
                                                    (float)Math.Sin((float)item.CTF.DefocusAngle * 2 * Helper.ToRad) * (float)item.CTF.DefocusDelta));

            // Calculate mean and stddev of all points in Cartesian coords
            if (AstigmatismPoints.Count > 0)
            {
                AstigmatismMean = new float2();
                AstigmatismMax = 0;
                foreach (var point in AstigmatismPoints)
                {
                    AstigmatismMean += point;
                    AstigmatismMax = Math.Max(AstigmatismMax, point.LengthSq());
                }
                AstigmatismMax = (float)Math.Sqrt(AstigmatismMax);
                AstigmatismMean /= AstigmatismPoints.Count;

                AstigmatismStd = 0;
                foreach (var point in AstigmatismPoints)
                    AstigmatismStd += (point - AstigmatismMean).LengthSq();
                AstigmatismStd = (float)Math.Max(1e-4, Math.Sqrt(AstigmatismStd / AstigmatismPoints.Count));
            }

            AstigmatismMax = Math.Max(1e-4f, (float)Math.Ceiling(AstigmatismMax * 20) / 20);

            // Set the labels for outer and inner circle
            // Dispatcher.InvokeAsync(() =>
            // {
            //     StatsAstigmatismLabelOuter.Value = (AstigmatismMax / StatsAstigmatismZoom).ToString("F3", CultureInfo.InvariantCulture);
            //     StatsAstigmatismLabelInner.Value = (AstigmatismMax / StatsAstigmatismZoom / 2).ToString("F3", CultureInfo.InvariantCulture);

            //     // Adjust plot axes
                
            //     PlotStatsAstigmatism.AxisMax = AstigmatismMax / StatsAstigmatismZoom;

            //     // Scale and position the valid range ellipse
            //     StatsAstigmatismEllipseSigma.Width = AstigmatismStd * StatsAstigmatismZoom * (float)Options.Filter.AstigmatismMax / AstigmatismMax * 256;
            //     StatsAstigmatismEllipseSigma.Height = AstigmatismStd * StatsAstigmatismZoom * (float)Options.Filter.AstigmatismMax / AstigmatismMax * 256;
            //     Canvas.SetLeft(StatsAstigmatismEllipseSigma, AstigmatismMean.X / AstigmatismMax * 128 * StatsAstigmatismZoom + 128 - StatsAstigmatismEllipseSigma.Width / 2);
            //     Canvas.SetTop(StatsAstigmatismEllipseSigma, AstigmatismMean.Y / AstigmatismMax * 128 * StatsAstigmatismZoom + 128 - StatsAstigmatismEllipseSigma.Height / 2);
            // });

            lock (Options)
            {
                Options.AstigmatismMean = AstigmatismMean;
                Options.AstigmatismStd = AstigmatismStd;
            }

            #endregion

            bool HaveCTF = Options.ProcessCTF || ItemsWithCTF.Length > 0;
            bool HavePhase = Options.CTF.DoPhase || ItemsWithCTF.Any(v => v.OptionsCTF.DoPhase);
            bool HaveMovement = Options.ProcessMovement || ItemsWithMovement.Length > 0;
            bool HaveParticles = Items.Any(m => m.HasAnyParticleSuffixes());

            // Dispatcher.InvokeAsync(() =>
            // {
            //     PanelStatsAstigmatism.Visibility = HaveCTF ? Visibility.Visible : Visibility.Collapsed;
            //     PanelStatsDefocus.Visibility = HaveCTF ? Visibility.Visible : Visibility.Collapsed;
            //     PanelStatsPhase.Visibility = HaveCTF && HavePhase ? Visibility.Visible : Visibility.Collapsed;
            //     PanelStatsResolution.Visibility = HaveCTF ? Visibility.Visible : Visibility.Collapsed;
            //     PanelStatsMotion.Visibility = HaveMovement ? Visibility.Visible : Visibility.Collapsed;
            //     PanelStatsParticles.Visibility = HaveParticles ? Visibility.Visible : Visibility.Collapsed;
            // });
        }

        private void UpdateFilterResult()
        {
            Movie[] Items = FileDiscoverer.GetImmutableFiles();

            float2 AstigmatismMean;
            float AstigmatismStd;
            lock (Options)
            {
                AstigmatismMean = Options.AstigmatismMean;
                AstigmatismStd = Options.AstigmatismStd;
            }

            foreach (var item in Items)
            {
                bool FilterStatus = true;

                if (item.OptionsCTF != null)
                {
                    FilterStatus &= item.CTF.Defocus >= Options.Filter.DefocusMin && item.CTF.Defocus <= Options.Filter.DefocusMax;
                    float AstigmatismDeviation = (new float2((float)Math.Cos((float)item.CTF.DefocusAngle * 2 * Helper.ToRad) * (float)item.CTF.DefocusDelta,
                                                             (float)Math.Sin((float)item.CTF.DefocusAngle * 2 * Helper.ToRad) * (float)item.CTF.DefocusDelta) - AstigmatismMean).Length() / AstigmatismStd;
                    FilterStatus &= AstigmatismDeviation <= (float)Options.Filter.AstigmatismMax;

                    FilterStatus &= item.CTFResolutionEstimate <= Options.Filter.ResolutionMax;

                    if (Options.CTF.DoPhase)
                        FilterStatus &= item.CTF.PhaseShift >= Options.Filter.PhaseMin && item.CTF.PhaseShift <= Options.Filter.PhaseMax;
                }

                if (item.OptionsMovement != null)
                {
                    FilterStatus &= item.MeanFrameMovement <= Options.Filter.MotionMax;
                }

                if (item.HasAnyParticleSuffixes())
                {
                    int Count = item.GetParticleCount(Options.Filter.ParticlesSuffix);
                    if (Count >= 0)
                        FilterStatus &= Count >= Options.Filter.ParticlesMin;
                }

                FilterStatus &= item.MaskPercentage <= Options.Filter.MaskPercentage;

                item.UnselectFilter = !FilterStatus;
            }

            // Calculate average CTF
            Task.Run(() =>
            {
                try
                {
                    CTF[] AllCTFs = Items.Where(m => m.OptionsCTF != null && !m.UnselectFilter).Select(m => m.CTF.GetCopy()).ToArray();
                    decimal PixelSize = Options.BinnedPixelSizeMean;

                    //Dispatcher.Invoke(() => StatsDefocusAverageCTFFrequencyLabel.Text = $"1/{PixelSize:F1} ??");

                    float[] AverageCTFValues = new float[192];
                    foreach (var ctf in AllCTFs)
                    {
                        ctf.PixelSize = PixelSize;
                        float[] Simulated = ctf.Get1D(AverageCTFValues.Length, true);

                        for (int i = 0; i < Simulated.Length; i++)
                            AverageCTFValues[i] += Simulated[i];
                    }

                    if (AllCTFs.Length > 1)
                        for (int i = 0; i < AverageCTFValues.Length; i++)
                            AverageCTFValues[i] /= AllCTFs.Length;

                    float MinAverage = MathHelper.Min(AverageCTFValues);

                    // Dispatcher.Invoke(() =>
                    // {
                    //     IEnumerable<Point> TrackPoints = AverageCTFValues.Select((v, i) => new Point(i, 24 - 1 - (24 * v)));

                    //     System.Windows.Shapes.Path TrackPath = new System.Windows.Shapes.Path()
                    //     {
                    //         Stroke = StatsDefocusAverageCTFFrequencyLabel.Foreground,
                    //         StrokeThickness = 1,
                    //         StrokeLineJoin = PenLineJoin.Bevel,
                    //         IsHitTestVisible = false
                    //     };
                    //     PolyLineSegment PlotSegment = new PolyLineSegment(TrackPoints, true);
                    //     PathFigure PlotFigure = new PathFigure
                    //     {
                    //         Segments = new PathSegmentCollection { PlotSegment },
                    //         StartPoint = TrackPoints.First()
                    //     };
                    //     TrackPath.Data = new PathGeometry { Figures = new PathFigureCollection { PlotFigure } };

                    //     StatsDefocusAverageCTFCanvas.Children.Clear();
                    //     StatsDefocusAverageCTFCanvas.Children.Add(TrackPath);
                    //     Canvas.SetBottom(TrackPath, 24 * MinAverage);
                    // });
                }
                catch { }
            });
        }

        public void UpdateFilterSuffixMenu()
        {
            Movie[] Items = FileDiscoverer.GetImmutableFiles();
            List<string> Suffixes = new List<string>();

            foreach (var movie in Items)
                foreach (var suffix in movie.GetParticlesSuffixes())
                    if (!Suffixes.Contains(suffix))
                        Suffixes.Add(suffix);

            Suffixes.Sort();
            // Dispatcher.InvokeAsync(() =>
            // {
            //     MenuParticlesSuffix.Items.Clear();
            //     foreach (var suffix in Suffixes)
            //         MenuParticlesSuffix.Items.Add(suffix);

            //     if ((string.IsNullOrEmpty(Options.Filter.ParticlesSuffix) || !Suffixes.Contains(Options.Filter.ParticlesSuffix))
            //         && Suffixes.Count > 0)
            //         Options.Filter.ParticlesSuffix = Suffixes[0];
            // });
        }

        public void UpdateBenchmarkTimes()
        {
            // Dispatcher.Invoke(() =>
            // {
            //     StatsBenchmarkOverall.Text = "";

            //     if (BenchmarkAllProcessing.NItems < 5)
            //         return;

            //     int NMeasurements = Math.Min(BenchmarkAllProcessing.NItems, 100);

            //     StatsBenchmarkOverall.Text = ((int)Math.Round(BenchmarkAllProcessing.GetPerSecondConcurrent(NMeasurements) * 3600)) + "???/???h";

            //     StatsBenchmarkInput.Text = BenchmarkRead.NItems > 0 ? (BenchmarkRead.GetAverageMilliseconds(NMeasurements) / 1000).ToString("F1") + "???s" : "";
            //     StatsBenchmarkCTF.Text = BenchmarkCTF.NItems > 0 ? (BenchmarkCTF.GetAverageMilliseconds(NMeasurements) / 1000).ToString("F1") + "???s" : "";
            //     StatsBenchmarkMotion.Text = BenchmarkMotion.NItems > 0 ? (BenchmarkMotion.GetAverageMilliseconds(NMeasurements) / 1000).ToString("F1") + "???s" : "";
            //     StatsBenchmarkPicking.Text = BenchmarkPicking.NItems > 0 ? (BenchmarkPicking.GetAverageMilliseconds(NMeasurements) / 1000).ToString("F1") + "???s" : "";
            //     StatsBenchmarkOutput.Text = BenchmarkOutput.NItems > 0 ? (BenchmarkOutput.GetAverageMilliseconds(NMeasurements) / 1000).ToString("F1") + "???s" : "";
            // });
        }
        #endregion

        #region File Discoverer

        private void FileDiscoverer_FilesChanged()
        {
            Movie[] ImmutableItems = null;
            Helper.Time("FileDiscoverer.GetImmutableFiles", () => ImmutableItems = FileDiscoverer.GetImmutableFiles());
        }

        private void FileDiscoverer_IncubationStarted()
        {
            
        }

        private void FileDiscoverer_IncubationEnded()
        {
            
        }

        #endregion

        public List<int> GetDeviceList()
        {
            List<int> Devices = new List<int>();
            
            
            // for (int i = 0; i < GPU.GetDeviceCount(); i++)
            //    Devices.Add(i);
	        Devices.Add(0);
            
	        // Devices.Add(5);
	        // Devices.Add(6);
            return Devices;
        }
    }
}
