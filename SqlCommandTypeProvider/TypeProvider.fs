﻿namespace FSharp.Data.SqlClient

open System
open System.Data
open System.Data.SqlClient
open System.Reflection
open System.Diagnostics

open Microsoft.FSharp.Core.CompilerServices
open Microsoft.FSharp.Quotations
open Microsoft.FSharp.Quotations.Patterns
open Microsoft.FSharp.Reflection
open Samples.FSharp.ProvidedTypes

type ResultSetType =
    | Tuples = 0
    | Records = 1
    | DataTable = 3

[<TypeProvider>]
type public SqlCommandTypeProvider(config : TypeProviderConfig) as this = 
    inherit TypeProviderForNamespaces()

    let runtimeAssembly = Assembly.LoadFrom(config.RuntimeAssembly)

    let nameSpace = this.GetType().Namespace
    let assembly = Assembly.GetExecutingAssembly()
    let providerType = ProvidedTypeDefinition(assembly, nameSpace, "SqlCommand", Some typeof<obj>, HideObjectMethods = true)

    do 
        providerType.DefineStaticParameters(
            parameters = [ 
                ProvidedStaticParameter("CommandText", typeof<string>) 
                ProvidedStaticParameter("ConnectionString", typeof<string>, "") 
                ProvidedStaticParameter("ConnectionStringName", typeof<string>, "") 
                ProvidedStaticParameter("CommandType", typeof<CommandType>, CommandType.Text) 
                ProvidedStaticParameter("ResultSetType", typeof<ResultSetType>, ResultSetType.Tuples) 
                ProvidedStaticParameter("SingleRow", typeof<bool>, false) 
                ProvidedStaticParameter("ConfigFile", typeof<string>, "app.config") 
                ProvidedStaticParameter("DataDirectory", typeof<string>, "") 
            ],             
            instantiationFunction = this.CreateType
        )
        this.AddNamespace(nameSpace, [ providerType ])

    member internal this.CreateType typeName parameters = 
        let commandText : string = unbox parameters.[0] 
        let connectionStringProvided : string = unbox parameters.[1] 
        let connectionStringName : string = unbox parameters.[2] 
        let commandType : CommandType = unbox parameters.[3] 
        let resultSetType : ResultSetType = unbox parameters.[4] 
        let singleRow : bool = unbox parameters.[5] 
        let configFile : string = unbox parameters.[6] 
        let dataDirectory : string = unbox parameters.[7] 

        let resolutionFolder = config.ResolutionFolder
        let connectionRelated = resolutionFolder,connectionStringProvided,connectionStringName,configFile
        let connectionString =  Configuration.getConnectionString connectionRelated

        using(new SqlConnection(connectionString)) <| fun conn ->
            conn.Open()
            conn.CheckVersion()
            conn.LoadDataTypesMap()
       
        let isStoredProcedure = commandType = CommandType.StoredProcedure

        let parameters = this.ExtractParameters(connectionString, commandText, isStoredProcedure)

        let genCommandType = ProvidedTypeDefinition(assembly, nameSpace, typeName, baseType = Some typeof<obj>, HideObjectMethods = true)


        ProvidedConstructor(
            parameters = [],
            InvokeCode = fun _ -> 
                <@@ 
                    let connectionString = Configuration.getConnectionString (resolutionFolder,connectionStringProvided,connectionStringName,configFile)

                    do
                        if dataDirectory <> ""
                        then AppDomain.CurrentDomain.SetData("DataDirectory", dataDirectory)

                    let this = new SqlCommand(commandText, new SqlConnection(connectionString)) 
                    this.CommandType <- commandType
                    for x in parameters do
                        let xs = x.Split(',') 
                        let paramName = xs.[0]
                        let sqlDbType = xs.[2] |> int |> enum
                        let direction = Enum.Parse(typeof<ParameterDirection>, xs.[3]) 
                        let p = SqlParameter(paramName, sqlDbType, Direction = unbox direction)
                        this.Parameters.Add p |> ignore
                    this
                @@>
        ) 
        |> genCommandType.AddMember 

        genCommandType.AddMembersDelayed <| fun() -> 
            parameters
            |> List.map (fun x -> 
                let paramName, clrTypeName, direction = 
                    let xs = x.Split(',') 
                    let success, direction = Enum.TryParse xs.[3]
                    assert success
                    xs.[0], xs.[1], direction

                assert (paramName.StartsWith "@")

                let propertyName = if direction = ParameterDirection.ReturnValue then "SpReturnValue" else paramName.Substring 1
                let prop = ProvidedProperty(propertyName, propertyType = Type.GetType clrTypeName)
                if direction = ParameterDirection.Output || direction = ParameterDirection.InputOutput || direction = ParameterDirection.ReturnValue
                then 
                    prop.GetterCode <- fun args -> 
                        <@@ 
                            let sqlCommand : SqlCommand = %%Expr.Coerce(args.[0], typeof<SqlCommand>)
                            sqlCommand.Parameters.[paramName].Value
                        @@>

                if direction = ParameterDirection.Input
                then 
                    prop.SetterCode <- fun args -> 
                        <@@ 
                            let sqlCommand : SqlCommand = %%Expr.Coerce(args.[0], typeof<SqlCommand>)
                            sqlCommand.Parameters.[paramName].Value <- %%Expr.Coerce(args.[1], typeof<obj>)
                        @@>

                prop
            )

        use conn = new SqlConnection(connectionString)
        use cmd = new SqlCommand("sys.sp_describe_first_result_set", conn, CommandType = CommandType.StoredProcedure)
        cmd.Parameters.AddWithValue("@tsql", commandText) |> ignore
        conn.Open()
        use reader = cmd.ExecuteReader()
        if reader.HasRows
        then 
            this.AddExecuteReader(reader, genCommandType, resultSetType, singleRow, connectionRelated, commandText)            
        else
           this.AddExecuteNonQuery (genCommandType, connectionString)

        genCommandType
    
    member __.ExtractParameters(connectionString, commandText, isStoredProcedure) : string list =  
        [
            use conn = new SqlConnection(connectionString)
            conn.Open()

            if isStoredProcedure
            then
                //quick solution for now. Maybe better to use conn.GetSchema("ProcedureParameters")
                use cmd = new SqlCommand(commandText, conn, CommandType = CommandType.StoredProcedure)
                SqlCommandBuilder.DeriveParameters cmd
                for p in cmd.Parameters do
                    let clrTypeName = findBySqlDbType p.SqlDbType
                    yield sprintf "%s,%s,%i,%O" p.ParameterName clrTypeName (int p.SqlDbType) p.Direction 
            else
                use cmd = new SqlCommand("sys.sp_describe_undeclared_parameters", conn, CommandType = CommandType.StoredProcedure)
                cmd.Parameters.AddWithValue("@tsql", commandText) |> ignore
                use reader = cmd.ExecuteReader()
                while(reader.Read()) do
                    let paramName = string reader.["name"]
                    let sqlEngineTypeId = unbox<int> reader.["suggested_system_type_id"]
                    let detailedMessage = " Parameter name:" + paramName
                    let clrTypeName, sqlDbTypeId = mapSqlEngineTypeId(sqlEngineTypeId, detailedMessage)
                    let direction = 
                        let output = unbox reader.["suggested_is_output"]
                        let input = unbox reader.["suggested_is_input"]
                        if input && output then ParameterDirection.InputOutput
                        elif output then ParameterDirection.Output
                        else ParameterDirection.Input

                    yield sprintf "%s,%s,%i,%O" paramName clrTypeName sqlDbTypeId direction
        ]


    member internal __.AddExecuteNonQuery(commandType, connectionString) = 
        let execute = ProvidedMethod("Execute", [], typeof<Async<int>>)
        execute.InvokeCode <- fun args ->
            <@@
                async {
                    let sqlCommand = %%Expr.Coerce(args.[0], typeof<SqlCommand>) : SqlCommand
                    //open connection async on .NET 4.5
                    use conn = new SqlConnection(connectionString)
                    conn.Open()
                    sqlCommand.Connection <- conn
                    return! sqlCommand.AsyncExecuteNonQuery() 
                }
            @@>
        commandType.AddMember execute

    static member internal GetDataReader(cmd, singleRow) = 
        let commandBehavior = if singleRow then CommandBehavior.SingleRow  else CommandBehavior.Default 
        <@@ 
            async {
                let sqlCommand : SqlCommand = %%Expr.Coerce(cmd, typeof<SqlCommand>)
                //open connection async on .NET 4.5
                sqlCommand.Connection.Open()
                return!
                    try 
                        sqlCommand.AsyncExecuteReader(commandBehavior ||| CommandBehavior.CloseConnection ||| CommandBehavior.SingleResult)
                    with _ ->
                        sqlCommand.Connection.Close()
                        reraise()
            }
        @@>

    static member internal GetRows(cmd, singleRow) = 
        let commandBehavior = if singleRow then CommandBehavior.SingleRow  else CommandBehavior.Default 
        <@@ 
            async {
                let! token = Async.CancellationToken
                let! (reader : SqlDataReader) = %%SqlCommandTypeProvider.GetDataReader(cmd, singleRow)
                return seq {
                    try 
                        while(not token.IsCancellationRequested && reader.Read()) do
                            let row = Array.zeroCreate reader.FieldCount
                            reader.GetValues row |> ignore
                            yield row  
                    finally
                        reader.Close()
                } |> Seq.cache
            }
        @@>

    static member internal GetTypedSequence<'Row>(cmd, rowMapper, singleRow) = 
        let getTypedSeqAsync = 
            <@@
                async { 
                    let! (rows : seq<obj[]>) = %%SqlCommandTypeProvider.GetRows(cmd, singleRow)
                    return Seq.map (%%rowMapper : obj[] -> 'Row) rows
                }
            @@>

        if singleRow
        then 
            <@@ 
                async { 
                    let! xs  = %%getTypedSeqAsync : Async<'Row seq>
                    return Seq.exactlyOne xs
                }
            @@>
        else
            getTypedSeqAsync
            

    static member internal SelectOnlyColumn0<'Row>(cmd, singleRow) = 
        SqlCommandTypeProvider.GetTypedSequence<'Row>(cmd, <@ fun (values : obj[]) -> unbox<'Row> values.[0] @>, singleRow)

    static member internal GetTypedDataTable<'T when 'T :> DataRow>(cmd, singleRow)  = 
        <@@
            async {
                use! reader = %%SqlCommandTypeProvider.GetDataReader(cmd, singleRow) : Async<SqlDataReader >
                let table = new DataTable<'T>() 
                table.Load reader
                return table
            }
        @@>

    static member internal Update(connectionRelated, commandText) = 
        let (resolutionFolder,connectionStringProvided,connectionStringName,configFile) = connectionRelated
        let result = ProvidedMethod("Update", [], typeof<int>) 
        result.InvokeCode <- fun args ->
            <@@
                let connectionString =  Configuration.getConnectionString (resolutionFolder,connectionStringProvided,connectionStringName,configFile)
                let table = %%Expr.Coerce(args.[0], typeof<DataTable>) : DataTable
                let adapter = new SqlDataAdapter(commandText, connectionString)
                let builder = new SqlCommandBuilder(adapter)
                adapter.InsertCommand <- builder.GetInsertCommand()
                adapter.DeleteCommand <- builder.GetDeleteCommand()
                adapter.UpdateCommand <- builder.GetUpdateCommand()
                adapter.Update table
            @@>
        result

    member internal __.AddExecuteReader(columnInfoReader, commandType, resultSetType, singleRow, connectionRelated, commandText) = 
        let columns = [
            while columnInfoReader.Read() do
                let columnName = string columnInfoReader.["name"]
                let sqlEngineTypeId = unbox columnInfoReader.["system_type_id"]
                let detailedMessage = " Column name:" + columnName
                let clrTypeName = mapSqlEngineTypeId(sqlEngineTypeId, detailedMessage) |> fst
                yield columnName, clrTypeName, unbox<int> columnInfoReader.["column_ordinal"]
        ] 
            
        let syncReturnType, executeMethodBody = 
            if columns.Length = 1
            then
                let _, itemTypeName, _ = columns.Head

                let itemType = Type.GetType itemTypeName
                let returnType = if singleRow then itemType else typedefof<_ seq>.MakeGenericType itemType 
                returnType, this.GetExecuteBoby("SelectOnlyColumn0", itemType, singleRow)

            else 
                match resultSetType with 

                | ResultSetType.Tuples ->
                    let tupleType = columns |> List.map (fun(_, typeName, _) -> Type.GetType typeName) |> List.toArray |> FSharpType.MakeTupleType
                    let rowMapper = 
                        let values = Var("values", typeof<obj[]>)
                        let getTupleType = Expr.Call(typeof<Type>.GetMethod("GetType", [| typeof<string>|]), [ Expr.Value tupleType.AssemblyQualifiedName ])
                        Expr.Lambda(values, Expr.Coerce(Expr.Call(typeof<FSharpValue>.GetMethod("MakeTuple"), [Expr.Var values; getTupleType]), tupleType))

                    let resultType = if singleRow then tupleType else typedefof<_ seq>.MakeGenericType(tupleType)
                    resultType, this.GetExecuteBoby("GetTypedSequence", tupleType, rowMapper, singleRow)

                | ResultSetType.Records -> 
                    let rowType = ProvidedTypeDefinition("Row", baseType = Some typeof<obj>, HideObjectMethods = true)
                    for name, propertyTypeName, columnOrdinal  in columns do
                        if name = "" then failwithf "Column #%i doesn't have name. Only columns with names accepted. Use explicit alias." columnOrdinal
                        let property = ProvidedProperty(name, propertyType = Type.GetType propertyTypeName) 
                        property.GetterCode <- fun args -> 
                            <@@ 
                                let values : obj[] = %%Expr.Coerce(args.[0], typeof<obj[]>)
                                values.[columnOrdinal - 1]
                            @@>

                        rowType.AddMember property

                    commandType.AddMember rowType
                    let resultType = if singleRow then rowType :> Type else typedefof<_ seq>.MakeGenericType(rowType)
                    let getExecuteBody (args : Expr list) = 
                        SqlCommandTypeProvider.GetTypedSequence(args.[0], <@ fun(values : obj[]) -> box values @>, singleRow)
                         
                    resultType, getExecuteBody

                | ResultSetType.DataTable ->
                    let rowType = ProvidedTypeDefinition("Row", Some typeof<DataRow>)
                    for name, propertyTypeName, columnOrdinal  in columns do
                        if name = "" then failwithf "Column #%i doesn't have name. Only columns with names accepted. Use explicit alias." columnOrdinal
                        let propertyType = Type.GetType propertyTypeName
                        let property = ProvidedProperty(name, propertyType) 
                        property.GetterCode <- fun args -> <@@ (%%args.[0] : DataRow).[name] @@>
                        property.SetterCode <- fun args -> <@@ (%%args.[0] : DataRow).[name] <- %%Expr.Coerce(args.[1],  typeof<obj>) @@>

                        rowType.AddMember property

                    let resultType = ProvidedTypeDefinition("Table", typedefof<_ DataTable>.MakeGenericType rowType |> Some ) 
                    
                    SqlCommandTypeProvider.Update(connectionRelated, commandText) |> resultType.AddMember 
                    commandType.AddMembers [ rowType :> Type; resultType :> Type ]

                    resultType :> Type, this.GetExecuteBoby("GetTypedDataTable",  typeof<DataRow>, singleRow)

                | _ -> failwith "Unexpected"
                    
        commandType.AddMember <| ProvidedMethod("Execute", [], typedefof<_ Async>.MakeGenericType syncReturnType, InvokeCode = executeMethodBody)

    member internal this.GetExecuteBoby(methodName, specialization, [<ParamArray>] bodyFactoryArgs : obj[]) =

        let impl = 
            let mi = this.GetType().GetMethod(methodName, BindingFlags.NonPublic ||| BindingFlags.Static)
            assert(mi <> null)
            mi.MakeGenericMethod([| specialization |])

        fun(args : Expr list) -> 
            impl.Invoke(null, [| yield box args.[0]; yield! bodyFactoryArgs |]) |> unbox

