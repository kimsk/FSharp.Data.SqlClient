module FSharp.Data.TypeProviderTest

open System
open System.Data
open System.Data.SqlClient
open Xunit
open FsUnit.Xunit

[<Literal>]
let connection = ConnectionStrings.AdventureWorksNamed

type GetEvenNumbers = SqlCommandProvider<"select * from (values (2), (4), (8), (24)) as T(value)", connection>

[<Fact>]
let asyncSinlgeColumn() = 
    Assert.Equal<int[]>([| 2; 4; 8; 24 |], (new GetEvenNumbers()).AsyncExecute() |> Async.RunSynchronously |> Seq.toArray)    

[<Fact>]
let ConnectionClose() = 
    use cmd = new GetEvenNumbers()
    let untypedCmd : ISqlCommand = upcast cmd
    let underlyingConnection = untypedCmd.Raw.Connection
    Assert.Equal(ConnectionState.Closed, underlyingConnection.State)
    Assert.Equal<int[]>([| 2; 4; 8;  24 |], cmd.Execute() |> Seq.toArray)    
    Assert.Equal(ConnectionState.Closed, underlyingConnection.State)

[<Fact>]
let ExternalInstanceConnection() = 
    use conn = new SqlConnection(ConnectionStrings.AdventureWorksLiteral)
    use cmd = new GetEvenNumbers()
    let untypedCmd : ISqlCommand = upcast cmd
    let underlyingConnection = untypedCmd.Raw.Connection
    Assert.Equal(ConnectionState.Closed, underlyingConnection.State)
    Assert.Equal<int[]>([| 2; 4; 8;  24 |], cmd.Execute() |> Seq.toArray)    
    Assert.Equal(ConnectionState.Closed, underlyingConnection.State)


type QueryWithTinyInt = SqlCommandProvider<"SELECT CAST(10 AS TINYINT) AS Value", connection, SingleRow = true>

[<Fact>]
let TinyIntConversion() = 
    use cmd = new QueryWithTinyInt()
    Assert.Equal(Some 10uy, cmd.Execute().Value)    

type ConvertToBool = SqlCommandProvider<"IF @Bit = 1 SELECT 'TRUE' ELSE SELECT 'FALSE'", connection, SingleRow=true>

[<Fact>]
let SqlCommandClone() = 
    use cmd = new ConvertToBool()
    Assert.Equal(Some "TRUE", cmd.Execute(Bit = 1))    
    let cmdClone = cmd.AsSqlCommand()
    cmdClone.Connection.Open()
    Assert.Throws<SqlClient.SqlException>(cmdClone.ExecuteScalar) |> ignore
    cmdClone.Parameters.["@Bit"].Value <- 1
    Assert.Equal(box "TRUE", cmdClone.ExecuteScalar())    
    Assert.Equal(cmdClone.ExecuteScalar(), cmd.Execute(Bit = 1).Value)    
    Assert.Equal(Some "FALSE", cmd.Execute(Bit = 0))    
    Assert.Equal(box "TRUE", cmdClone.ExecuteScalar())    
    cmdClone.CommandText <- "SELECT 0"
    Assert.Equal(Some "TRUE", cmd.Execute(Bit = 1))    

type ConditionalQuery = SqlCommandProvider<"IF @flag = 0 SELECT 1, 'monkey' ELSE SELECT 2, 'donkey'", connection, SingleRow=true, ResultType = ResultType.Tuples>

[<Fact>]
let ConditionalQuery() = 
    let cmd = new ConditionalQuery()
    Assert.Equal(Some(1, "monkey"), cmd.Execute(flag = 0))    
    Assert.Equal(Some(2, "donkey"), cmd.Execute(flag = 1))    

type ColumnsShouldNotBeNull2 = 
    SqlCommandProvider<"SELECT COLUMN_NAME, IS_NULLABLE, DATA_TYPE, CHARACTER_MAXIMUM_LENGTH, NUMERIC_PRECISION
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_NAME = 'DatabaseLog' and numeric_precision is null
            ORDER BY ORDINAL_POSITION", connection, SingleRow = true, ResultType = ResultType.Tuples>

[<Fact>]
let columnsShouldNotBeNull2() = 
    let _,_,_,_,precision = ColumnsShouldNotBeNull2.Create().Execute() |> Option.get
    Assert.Equal(None, precision)    

[<Literal>]
let bitCoinCode = "BTC"
[<Literal>]
let bitCoinName = "Bitcoin"

type DeleteBitCoin = SqlCommandProvider<"DELETE FROM Sales.Currency WHERE CurrencyCode = @Code", connection>
type InsertBitCoin = SqlCommandProvider<"INSERT INTO Sales.Currency VALUES(@Code, @Name, GETDATE())", connection>
type GetBitCoin = SqlCommandProvider<"SELECT CurrencyCode, Name FROM Sales.Currency WHERE CurrencyCode = @code", connection>

[<Fact>]
let asyncCustomRecord() =
    (new GetBitCoin()).AsyncExecute("USD") |> Async.RunSynchronously |> Seq.length |> should equal 1

type NoneSingleton = SqlCommandProvider<"select 1 where 1 = 0", connection, SingleRow = true>
type SomeSingleton = SqlCommandProvider<"select 1", connection, SingleRow = true>

[<Fact>]
let singleRowOption() =
    (new NoneSingleton()).Execute().IsNone |> should be True
    (new SomeSingleton()).AsyncExecute() |> Async.RunSynchronously |> should equal (Some 1)


type Echo = SqlCommandProvider<"SELECT CAST(@Date AS DATE), CAST(@Number AS INT)", connection, ResultType.Tuples>

[<Fact>]
let ToTraceString() =
    let now = DateTime.Now
    let num = 42
    let expected = sprintf "exec sp_executesql N'SELECT CAST(@Date AS DATE), CAST(@Number AS INT)',N'@Date Date,@Number Int',@Date='%A',@Number='%d'" now num
    let cmd = new Echo()
    cmd.ToTraceString( now, num) |> should equal expected

[<Fact>]
let ``ToTraceString for CRUD``() =    
    (new GetBitCoin()).ToTraceString(bitCoinCode) 
    |> should equal "exec sp_executesql N'SELECT CurrencyCode, Name FROM Sales.Currency WHERE CurrencyCode = @code',N'@code NChar(3)',@code='BTC'"
    
    (new InsertBitCoin()).ToTraceString(bitCoinCode, bitCoinName) 
    |> should equal "exec sp_executesql N'INSERT INTO Sales.Currency VALUES(@Code, @Name, GETDATE())',N'@Code NChar(3),@Name NVarChar(7)',@Code='BTC',@Name='Bitcoin'"
    
    (new DeleteBitCoin()).ToTraceString(bitCoinCode) 
    |> should equal "exec sp_executesql N'DELETE FROM Sales.Currency WHERE CurrencyCode = @Code',N'@Code NChar(3)',@Code='BTC'"

type GetObjectId = SqlCommandProvider<"SELECT OBJECT_ID('Sales.Currency')", connection>
[<Fact>]
let ``ToTraceString double-quotes``() =    
    use cmd = new GetObjectId()
    let trace = cmd.ToTraceString()
    Assert.Equal<string>("exec sp_executesql N'SELECT OBJECT_ID(''Sales.Currency'')'", trace)

type LongRunning = SqlCommandProvider<"WAITFOR DELAY '00:00:35'; SELECT 42", connection, SingleRow = true>
[<Fact(
    Skip = "Don't execute for usual runs. Too slow."
    )>]
let CommandTimeout() =
    use cmd = new LongRunning(commandTimeout = 60)
    Assert.Equal(60, cmd.CommandTimeout)
    Assert.Equal(Some 42, cmd.Execute())     

type DynamicCommand = SqlCommandProvider<"
	    DECLARE @stmt AS NVARCHAR(MAX) = @tsql
	    DECLARE @params AS NVARCHAR(MAX) = N'@p1 nvarchar(100)'
	    DECLARE @p1 AS NVARCHAR(100) = @firstName
	    EXECUTE sp_executesql @stmt, @params, @p1
	    WITH RESULT SETS
	    (
		    (
			    Name NVARCHAR(100)
			    ,UUID UNIQUEIDENTIFIER 
		    )
	    )
    ", connection>

[<Fact>]
let DynamicSql() =    
    let cmd = new DynamicCommand()
    //provide dynamic sql query with param
    cmd.Execute("SELECT CONCAT(FirstName, LastName) AS Name, rowguid AS UUID FROM Person.Person WHERE FirstName = @p1", "Alex") |> Seq.toArray |> Array.length |> should equal 51
    //extend where condition by filetering out additional rows
    cmd.Execute("SELECT CONCAT(FirstName, LastName) AS Name, rowguid AS UUID FROM Person.Person WHERE FirstName = @p1 AND EmailPromotion = 2", "Alex") |> Seq.toArray |> Array.length |> should equal 9
    //accessing completely diff table
    cmd.Execute("SELECT Name, rowguid AS UUID FROM Production.Product WHERE Name = @p1", "Chainring Nut") |> Seq.toArray |> Array.length |> should equal 1

type DeleteStatement = SqlCommandProvider<"
    DECLARE @myTable TABLE( id INT)
    INSERT INTO @myTable VALUES (42)
    DELETE FROM @myTable
    ", connection>

[<Fact>]
let DeleteStatement() =    
    use cmd = new DeleteStatement(ConnectionStrings.AdventureWorksLiteral)
    Assert.Equal(2, cmd.Execute())