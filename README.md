# RadCapToLocalhostReplicator

Replicates RadCap AAC radio stream to the localhost port. Extracts current song info from the stream and writes updates to the file.

How to use:
1. Edit *setting.json* to specify desired radio station url, localhost port for stream replication, file for song info updates.
2. Run the program.
3. In Obs Studio - `Sources > Add > Media Source` uncheck `Local File` and set `Input` to `http://localhost:port/` as in *settings*.
4. In Obs Studio - `Sources > Add > Text` check `Read from file` and target the file from *settings*. 
