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

using System.Collections.ObjectModel;
using System.Diagnostics.Contracts; 
using System.IO;
using System.Text;
using System.Xml;
using System.Xml.XPath;

namespace Warp{
    public class Program{
        public readonly FileDiscoverer FileDiscoverer;
        string starPath, starFilePath;
        Options options;

        public async Task discoverReady(){
            FileDiscoverer.ChangePath(starPath, "*.tomostar"); // CUSTOM VKJY
            await Task.Delay(500); // For wait to set FileDiscoverer!!!!
        }
        TiltSeries[] getSeries(){
            TiltSeries[] Series = FileDiscoverer.GetImmutableFiles().Cast<TiltSeries>().ToArray();
            return Series;
        }
        async Task tomoReconstruct(TiltSeries[] Series){
            Console.WriteLine("Enter the name of the *star file [ Should not include the .star ]");
            // starFilePath = Console.ReadLine() + ".star";
            starFilePath = "/cdata/relion/Refine3D/job002/run_data_rln3.0" + ".star";
            TomoParticleExport tpe = new TomoParticleExport(Series, starFilePath, options);
            await tpe.WorkStart();
        }
        public Program(){
            #region Make sure everything is OK with GPUs
            options = new Options();
            options.MainWindow = this;
            System.Int32 gpuDeviceCountV = 0; // Options.Runtime.DeviceCount를 대체.
            try
            {
                options.Runtime.DeviceCount = GPU.GetDeviceCount();
                if (options.Runtime.DeviceCount <= 0){
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
            starPath = "/cdata/frames";
            Console.WriteLine("Your input is... {0}", starPath);

            #region File discoverer

            FileDiscoverer = new FileDiscoverer();
            FileDiscoverer.FilesChanged += FileDiscoverer_FilesChanged;
            FileDiscoverer.IncubationStarted += FileDiscoverer_IncubationStarted;
            FileDiscoverer.IncubationEnded += FileDiscoverer_IncubationEnded;
            
            #endregion
        }

        ~Program(){
            FileDiscoverer.Shutdown();
        }

        async static Task Main(string[] args){
            Console.WriteLine("-------------- Test in warpMain.cs --------------");
            // VKJY_RECONSTRUCT_SUBTOMO

            Program main = new Program();
            await main.discoverReady();
            TiltSeries[] Series = main.getSeries();


            for(int i=0; i<1; i++){
                Console.WriteLine("List Series size? : {0}", Series.Length);
                foreach (TiltSeries t in Series){
                    //Console.WriteLine("List output is... {0}", t.SubtomoDir);
                }
            }
            
            await main.tomoReconstruct(Series);
            return;
        }

        # region File Discoverer

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

            {
               // for (int i = 0; i < GPU.GetDeviceCount(); i++)
               //     Devices.Add(i);
	    }
	    Devices.Add(0);
            return Devices;
        }
    }
}
