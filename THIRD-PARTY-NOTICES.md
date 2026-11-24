# Componentes de terceros

MandanHeat incluye o usa los siguientes componentes. Cada uno se distribuye bajo su propia licencia.

| Componente | Uso | Licencia |
|---|---|---|
| [LibreHardwareMonitorLib](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) | Lectura de sensores (incluido en el ejecutable) | MPL-2.0 |
| [HidSharp](https://www.zer7.com/software/hidsharp) | Comunicación USB HID (refrigeraciones líquidas), dependencia de LibreHardwareMonitorLib | Apache-2.0 |
| DiskInfoToolkit, RAMSPDToolkit, BlackSharp.Core | Dependencias de LibreHardwareMonitorLib (discos y memoria) | MPL-2.0 |
| [.NET Runtime y WPF](https://github.com/dotnet) | Entorno de ejecución (incluido en el ejecutable) | MIT |
| [PresentMon](https://github.com/GameTechDev/PresentMon) (Intel) | Medición de FPS. **No se incluye**: la app lo descarga desde su página oficial si el usuario lo pide | MIT |
| [PawnIO](https://pawnio.eu/) | Driver de acceso a sensores. **No se incluye**: lo instala el usuario | Ver su web |

El código fuente de LibreHardwareMonitorLib (MPL-2.0) está disponible en su repositorio oficial. MandanHeat lo usa sin modificaciones, desde el paquete NuGet `LibreHardwareMonitorLib` 0.9.6.
