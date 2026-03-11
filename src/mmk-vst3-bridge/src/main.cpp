#include <iostream>

#include "bridge.h"

int main(int argc, char* argv[])
{
    if (argc < 2)
    {
        std::cerr << "Usage: mmk-vst3-bridge.exe <hostPid>\n";
        return 1;
    }

    try
    {
        const std::uint32_t hostPid = static_cast<std::uint32_t>(std::stoul(argv[1]));
        Bridge bridge(hostPid);
        bridge.Run();
    }
    catch (const std::exception& ex)
    {
        std::cerr << "Bridge error: " << ex.what() << "\n";
        return 1;
    }

    return 0;
}
