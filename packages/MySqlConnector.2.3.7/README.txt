This directory is reserved for the MySqlConnector 2.3.7 NuGet package contents.

Because the build environment here does not allow outbound downloads, the
package itself is not committed. To build offline, download the
MySqlConnector.2.3.7.nupkg from nuget.org and extract it here so that the
following files exist:

- packages/MySqlConnector.2.3.7/lib/net461/MySqlConnector.dll
- packages/MySqlConnector.2.3.7/build/net461/MySqlConnector.targets

You can also drop the .nupkg file at packages/MySqlConnector.2.3.7.nupkg and
run tools/fetch_mysqlconnector.sh to unpack it locally.
