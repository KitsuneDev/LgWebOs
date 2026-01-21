using Guss.Communications.ModuleFramework.Events.EventArguments;

namespace LgWebOs.Events
{
    public class StringEventArgs : GenericEventArgs<string>
    {
        public StringEventArgs()
        {
        }

        public StringEventArgs(string payload) : base(payload)
        {
            
        }
    }
}