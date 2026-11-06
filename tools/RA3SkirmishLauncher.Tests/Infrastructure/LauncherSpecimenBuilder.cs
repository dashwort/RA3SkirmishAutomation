using System.Text;
using AutoFixture.Kernel;

namespace RA3SkirmishLauncher.Tests.Infrastructure
{
    public sealed class LauncherSpecimenBuilder : ISpecimenBuilder
    {
        public object Create(object request, ISpecimenContext context)
        {
            if (request is Type type && type == typeof(PresetTestData))
            {
                return new PresetTestData
                {
                    Bytes = Encoding.UTF8.GetBytes("Rts:Map = data/maps/official/map_mp_2_black1b/map_mp_2_black1b.map\n" +
                        "Rts:GameInfo = M=map_mp_2_black1b;RU=1 5000;S=Hhuman;\nOther = preserve exactly\n"),
                    OtherBytes = Encoding.UTF8.GetBytes("Rts:Map = data/maps/official/map_mp_2_black1b/map_mp_2_black1b.map\n" +
                        "Rts:GameInfo = M=map_mp_2_black1b;RU=1 10000;S=Hhuman;\nOther = another preset\n")
                };
            }
            return new NoSpecimen();
        }
    }
}
