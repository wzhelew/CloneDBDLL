This directory is reserved for the MySqlConnector 2.3.7 NuGet package contents.

Because the build environment here does not allow outbound downloads, the
package itself is not committed. To build offline, download the
MySqlConnector.2.3.7.nupkg from nuget.org and extract it here so that the
following files exist:

- packages/MySqlConnector.2.3.7/lib/net462/MySqlConnector.dll (или lib/net461, ако net462 не е наличен) — подходящо за .NET Framework 4.8
- packages/MySqlConnector.2.3.7/build/net462/MySqlConnector.targets (или build/net461)

You can also drop the .nupkg file at packages/MySqlConnector.2.3.7.nupkg and
run tools/fetch_mysqlconnector.sh to unpack it locally.
