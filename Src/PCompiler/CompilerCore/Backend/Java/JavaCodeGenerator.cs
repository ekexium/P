using Plang.Compiler.TypeChecker;
using Plang.Compiler.TypeChecker.AST.Declarations;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks.Dataflow;
using Plang.Compiler.Backend.ASTExt;
using Plang.Compiler.TypeChecker.AST;
using Plang.Compiler.TypeChecker.AST.Expressions;
using Plang.Compiler.TypeChecker.AST.Statements;
using Plang.Compiler.TypeChecker.AST.States;

namespace Plang.Compiler.Backend.Java {

    public class JavaCodeGenerator : ICodeGenerator
    {

        private CompilationContext _context;
        private CompiledFile _source;
        private Scope _globalScope;

        // In the C# and Rvm compilers, ANONs' function signatures are hard-coded to
        // be an abstract Event type.  However, our Java runtime takes advantage of the
        // typechecker to be able to specify exactly _which_ subclass it is going to be
        // handed.  This isn't something explicitly handed to us in the AST so we
        // accumulate it ourselves before proceeding with code generation.
        private Dictionary<Function, PEvent> _argumentForAnon;

        /// <summary>
        /// Generates Java code for a given compilation job.
        ///
        /// Currently, we should be able to use nested classes to put everything we need in a single
        /// Java file, in a manner similar to how the C# extractor uses namespaces.
        /// </summary>
        /// <param name="job"></param>
        /// <param name="scope"></param>
        /// <returns></returns>
        /// <exception cref="Exception"></exception>
        public IEnumerable<CompiledFile> GenerateCode(ICompilationJob job, Scope scope)
        {
            _context = new CompilationContext(job);
            _source = new CompiledFile(_context.FileName);
            _globalScope = scope;
            
            _argumentForAnon = GenerateAnonArgLookup();

            WriteImports();
            WriteLine();

            foreach (var e in _globalScope.Enums)
            {
                WriteEnumDecl(e);
            }
            WriteLine();
            
            WriteLine($"static class {Constants.TopLevelClassName} {{");

            foreach (var e in _globalScope.Events)
            {
                WriteEventDecl(e);
            }
            WriteLine();
            
            //TODO: Do specs need interfaces?

            foreach (var m in _globalScope.Machines)
            {
                if (m.IsSpec)
                {
                    WriteMonitorDecl(m);
                }
                else
                {
                    WriteMachineDecl(m);
                }
            }
            
            WriteLine($"}} // {Constants.TopLevelClassName} class definition");
            
            return new List<CompiledFile> { _source };
        }

        private Dictionary<Function, PEvent> GenerateAnonArgLookup()
        {
            Dictionary<Function, PEvent> ret = new Dictionary<Function, PEvent>();


            foreach (var m in _globalScope.Machines)
            {
                foreach (var s in m.States)
                {
                    foreach (var (e, a) in s.AllEventHandlers)
                    {
                        switch (a)
                        {
                            case EventDoAction da:
                                ret.Add(da.Target, e);
                                break;
                            case EventGotoState gs when gs.TransitionFunction != null:
                                ret.Add(gs.TransitionFunction, e);
                                break;
                            case EventDefer _:
                            case EventIgnore i:
                            default:
                                break;
                        }
                    }
                }
            }

            return ret;
        }

        private void WriteImports()
        {
            foreach (var stmt in Constants.ImportStatements())
            {
                WriteLine(stmt);
            }
        }

        private void WriteEnumDecl(PEnum e)
        {
            WriteLine($"enum {e.Name} {{");
             
            foreach (var (param, sep)in e.Values.Select((p, i) => (p, i < e.Values.Count()-1 ? "," : ";")))
            {
                WriteLine($"{param.Name}({param.Value}){sep}");
            }

            WriteLine();
           
            // Boilerplate to access the enum's value
            WriteLine("private int val;");
            WriteLine($"{e.Name}(int i) {{ val = i; }}");
            WriteLine("public int getVal() { return val; }");
            
            WriteLine("}");
        }
        
        private void WriteEventDecl(PEvent e)
        {
            string eventName = _context.Names.GetNameForDecl(e);
            
            // FIXME: If e.PayloadType is PrimitiveType.Null, this produces an 
            // extraneous value.
            TypeManager.JType argType = _context.Types.JavaTypeFor(e.PayloadType);
            switch (argType)
            {
                case TypeManager.JType.JVoid _:
                    // Special-case an event with no payload: just emit an empty record.
                    WriteLine($"record {eventName}() implements PObserveEvent.PEvent {{ }} ");
                    break;
                default:
                    WriteLine($"record {eventName}({argType.TypeName} payload) implements PObserveEvent.PEvent {{ }} ");
                    break;
            }
            
        }

        private void WriteMachineDecl(Machine m)
        {
            WriteLine($"// PMachine {m.Name} elided ");
        }
        
        private void WriteMonitorDecl(Machine m)
        {
            string cname = _context.Names.GetNameForDecl(m);
            
            WriteLine($"static class {cname} extends Monitor {{");

            // monitor fields
            foreach (var field in m.Fields)
            {
                TypeManager.JType type = _context.Types.JavaTypeFor(field.Type);
                string name = _context.Names.GetNameForDecl(field);

                Write($"private {type.TypeName} {name} = {type.DefaultValue};");
            }
            WriteLine();

            // state identifiers
            foreach (var s in m.States)
            {
                //TODO: I think it's fine to use unqualified names here.  But, confirm.
                WriteLine($"private String {_context.Names.IdentForState(s)} = \"{s.Name}\";");
            }
            WriteLine();
            
            // functions
            foreach (var f in m.Methods)
            {
                WriteFunction(f);
            }
            WriteLine();
            
            // constructor
            WriteMonitorCstr(m);
            

            WriteLine($"}} // {cname} monitor definition");
        }


        private void WriteFunction(Function f)
        {
            if (f.IsForeign)
            {
                WriteLine($"// Foreign function {f.Name} elided");
            }

            if (f.CanReceive == true)
            {
                WriteLine($"// Async function {f.Name} elided");
            }
           
            WriteFunctionSignature(f); WriteLine(" {");

            if (f.IsAnon && f.Signature.Parameters.Any())
            {
                Variable p = f.Signature.Parameters.First();
                TypeManager.JType t = _context.Types.JavaTypeFor(p.Type);
                string name = _context.Names.GetNameForDecl(p);

                WriteLine($"{t.TypeName} {name} = pEvent.payload;");
            }
            
            foreach (var decl in f.LocalVariables)
            {
                //TODO: for reference types the default value can simply be null; it will be reassigned later.
                TypeManager.JType t = _context.Types.JavaTypeFor(decl.Type);
                WriteLine($"{t.TypeName} {_context.Names.GetNameForDecl(decl)} = {t.DefaultValue};");
            }
            WriteLine(); 
            
            foreach (var stmt in f.Body.Statements)
            {
                WriteStmt(stmt);
            }
            
            WriteLine("}");
        }

        private void WriteFunctionSignature(Function f)
        {
            string fname = _context.Names.GetNameForDecl(f);

            Write("private ");
            
            bool isStatic = f.Owner == null;
            if (isStatic)
            {
                Write("static ");
            }
            

            string args;
            if (f.IsAnon)
            {
                args = $"{_argumentForAnon[f].Name} pEvent";
            } 
            else 
            {
                args = string.Join(
                    ",",
                    f.Signature.Parameters.Select(v =>
                        $"{_context.Types.JavaTypeFor(v.Type).TypeName} {_context.Names.GetNameForDecl(v)}"));
            }

            TypeManager.JType retType = _context.Types.JavaTypeFor(f.Signature.ReturnType);
            
            Write($"{retType.TypeName} {fname}({args})");

            if (f.CanChangeState == true)
            {
                Write(" throws TransitionException");
            }
        }
        
        private void WriteMonitorCstr(Machine m)
        {
            string cname = _context.Names.GetNameForDecl(m);
            
            WriteLine($"public {cname}() {{");
            WriteLine("super();");
            
            foreach (var s in m.States)
            {   
                WriteStateBuilderDecl(s);
            }
            WriteLine("} // constructor");
        }
        
        private void WriteStateBuilderDecl(State s)
        {
            WriteLine($"addState(new State.Builder({_context.Names.IdentForState(s)})");
            WriteLine($".isInitialState({TypeManager.JType.JBool.ToJavaLiteral(s.IsStart)})");

            foreach (var (e, a) in s.AllEventHandlers)
            {
                WriteStateBuilderEventHandler(e, a);
            }
            
            WriteLine(".build());");
        }
        
        private void WriteStateBuilderEventHandler(PEvent e, IStateAction a)
        {
            string ename = _context.Names.GetNameForDecl(e);
           
            switch (a)
            {
                case EventDefer _:
                    WriteLine($"// Ignoring deferred event {ename}");
                    break;
                case EventDoAction da:
                {
                    string aname = _context.Names.GetNameForDecl(da.Target);
                    WriteLine($".withEvent({ename}.class, this::{aname})");
                    break;
                }
                case EventGotoState gs:
                {
                    string aname = _context.Names.GetNameForDecl(gs.Target);
                    WriteLine($".withEvent({ename}.class, __ -> gotoState({aname}))");
                    break;
                }
                case EventIgnore i:
                    WriteLine($".withEvent({ename}.class, __ -> {{ ; }})");
                    break;
                default:
                    throw new NotImplementedException($"TODO: {a.GetType()} not implemented.");
            }
        }

        private void WriteStmt(IPStmt stmt)
        {
            TypeManager.JType t;
            
            switch (stmt)
            {
                case AddStmt addStmt:
                    t = _context.Types.JavaTypeFor(addStmt.Variable.Type);
                    WriteExpr(addStmt.Variable);
                    Write($".{t.MutatorMethodName}(");
                    WriteExpr(addStmt.Value);
                    WriteLine(");");
                    break;
                
                case AssertStmt assertStmt:
                    Write("TryAssert(");
                    WriteExpr(assertStmt.Assertion);
                    Write(", ");
                    WriteExpr(assertStmt.Message);
                    WriteLine(");");
                    break;
                
                case AssignStmt assignStmt:
                    WriteAssignStatement(assignStmt);
                    break;
                
                case BreakStmt _:
                    WriteLine("break;");
                    break;
                
                case CompoundStmt compoundStmt:
                    WriteLine("{");
                    foreach (var s in compoundStmt.Statements)
                    {
                        WriteStmt(s);
                    }
                    WriteLine("}");
                    break;
               
                case ContinueStmt _:
                    WriteLine("continue;");
                    break;
                    
                case CtorStmt _:
                    goto default;
                    
                case FunCallStmt funCallStmt:
                    WriteFunctionCall(funCallStmt.Function, funCallStmt.ArgsList);
                    break;
                
                case GotoStmt gotoStmt:
                    WriteLine($"gotoState({_context.Names.IdentForState(gotoStmt.State)});");
                    WriteLine("return;");
                    break;
                    
                case IfStmt ifStmt:
                    Write("if (");
                    WriteExpr(ifStmt.Condition);
                    Write(") ");
                    
                    WriteStmt(ifStmt.ThenBranch);
                    if (ifStmt.ElseBranch != null && ifStmt.ElseBranch.Statements.Count > 0)
                    {
                        WriteLine(" else ");
                        WriteStmt(ifStmt.ElseBranch);
                    }
                    break;
                    
                
                case InsertStmt insertStmt:
                    t = _context.Types.JavaTypeFor(insertStmt.Variable.Type);
                    WriteExpr(insertStmt.Variable);
                    Write($".{t.MutatorMethodName}(");
                    WriteExpr(insertStmt.Value);
                    WriteLine(");");
                    break;
                    
                case MoveAssignStmt moveAssignStmt:
                    goto default;
                    
                case NoStmt _:
                    break;
                
                case PrintStmt printStmt:
                    Write("System.out.println(");
                    WriteExpr(printStmt.Message);
                    WriteLine(");");
                    break;
                
                case RaiseStmt raiseStmt:
                    goto default; // TODO
                    
                case ReceiveStmt receiveStmt:
                    goto default; 
                    
                case RemoveStmt removeStmt:
                    t = _context.Types.JavaTypeFor(removeStmt.Variable.Type);
                    WriteExpr(removeStmt.Variable);
                    Write($".{t.RemoveMethodName}(");
                    WriteExpr(removeStmt.Value);
                    WriteLine(");");
                    break;
                
                case ReturnStmt returnStmt:
                    Write("return ");
                    if (returnStmt.ReturnValue != null)
                    {
                        WriteExpr(returnStmt.ReturnValue);
                    }
                    WriteLine(";");
                    break;
                    
                case SendStmt sendStmt:
                    goto default;

                case ForeachStmt foreachStmt:
                {
                    string varname = _context.Names.GetNameForDecl(foreachStmt.Item);
                    t = _context.Types.JavaTypeFor(foreachStmt.Item.Type);
                    
                    Write($"for ({t.TypeName} {varname} : ");
                    WriteExpr(foreachStmt.IterCollection);
                    Write(") ");
                    WriteStmt(foreachStmt.Body);
                    break;
                }
                case WhileStmt whileStmt:
                    Write("while (");
                    WriteExpr(whileStmt.Condition);
                    Write(") ");
                    WriteStmt(whileStmt.Body);
                    break;
                
                case AnnounceStmt announceStmt:
                    goto default;
                    
                default:
                    WriteLine($"// TODO: {stmt}");
                    return;
                    //throw new NotImplementedException(stmt.GetType().ToString());
            }
        }

        private void WriteAssignStatement(AssignStmt assignStmt)
        {
            IPExpr lval = assignStmt.Location;
            TypeManager.JType t = _context.Types.JavaTypeForVarLocation(lval);
            
            IPExpr rval = assignStmt.Value;
            
            switch (lval)
            {
                case MapAccessExpr mapAccessExpr:
                    WriteExpr(mapAccessExpr.MapExpr);
                    Write($".{t.MutatorMethodName}(");
                    WriteExpr(mapAccessExpr.IndexExpr);
                    Write(",");
                    WriteExpr(rval);
                    WriteLine(");");
                    break;

                case NamedTupleAccessExpr namedTupleAccessExpr:
                    WriteExpr(namedTupleAccessExpr.SubExpr);
                    Write($".{t.MutatorMethodName}(\"{namedTupleAccessExpr.FieldName}\",");
                    WriteExpr(rval);
                    WriteLine(");");
                    break;

                case SeqAccessExpr seqAccessExpr:
                    // TODO: do we need to think about handling out of bounds exceptions?
                    WriteExpr(seqAccessExpr.SeqExpr);
                    Write($".{t.MutatorMethodName}(");
                    WriteExpr(seqAccessExpr.IndexExpr);
                    Write(",");
                    WriteExpr(rval);
                    WriteLine(");");
                    break;

                case TupleAccessExpr tupleAccessExpr:
                    WriteExpr(tupleAccessExpr.SubExpr);
                    Write($".put({tupleAccessExpr.FieldNo.ToString()}, ");
                    WriteExpr(rval);
                    WriteLine(");");
                    break;

                case VariableAccessExpr variableAccessExpr:
                    Write(_context.Names.GetNameForDecl(variableAccessExpr.Variable));
                    Write(" = ");
                    WriteExpr(rval);
                    WriteLine(";");
                    break;
            }
        }

        private void WriteFunctionCall(Function f, IEnumerable<IPExpr> args)
        {
            if (f.Owner == null)
            {
                throw new NotImplementedException("StaticFunCallExpr is not implemented.");
            }

            string fname = _context.Names.GetNameForDecl(f);
            
            Write($"{fname}(");
            foreach (var (param, sep)in args.Select((p, i) => (p, i > 0 ? ", " : "")))
            {
                Write(sep);
                WriteExpr(param);
            }
            Write($")");
        }

        private void WriteExpr(IPExpr expr)
        {
            TypeManager.JType t;
            
            switch (expr)
            {
                case BinOpExpr binOpExpr:
                    WriteBinOp(binOpExpr.Lhs, binOpExpr.Operation, binOpExpr.Rhs);
                    break;
                case BoolLiteralExpr ble:
                    Write($"({TypeManager.JType.JBool.ToJavaLiteral(ble.Value)})");
                    break;
                case CastExpr ce:
                    t = _context.Types.JavaTypeFor(ce.Type);
                    //TODO: I am 99% sure it's fine to never worry about casting to the boxed type.
                    Write($"(");
                    Write($"({t.TypeName})");
                    WriteExpr(ce.SubExpr);
                    Write($")");
                    break;
                case ChooseExpr ce:
                    goto default; //TODO
                case CloneExpr ce:
                    WriteClone(ce);
                    break;
                case CoerceExpr ce:
                    goto default; //TODO
                case ContainsExpr ce:
                    t = _context.Types.JavaTypeFor(ce.Collection.Type);
                    // TODO: uh oh, do nested containers etc mean we actually need to do
                    // unchecked casting at every step??
                    WriteExpr(ce.Collection);
                    Write($".{t.ContainsMethodName}()");
                    break;
                case CtorExpr ce:
                    goto default;
                case DefaultExpr de:
                    t = _context.Types.JavaTypeFor(de.Type);
                    Write(t.DefaultValue);
                    break;
                case EnumElemRefExpr ee:
                    string typeName = ee.Value.ParentEnum.Name;
                    string valueName = ee.Value.Name;
                    Write($"{typeName}.{valueName}");
                    break;
                case EventRefExpr ee:
                    goto default; //TODO
                case FairNondetExpr fe:
                    goto default;
                case FloatLiteralExpr fe:
                    Write(TypeManager.JType.JFloat.ToJavaLiteral(fe.Value));
                    break;
                case FunCallExpr fe:
                    WriteFunctionCall(fe.Function, fe.Arguments);
                    break;
                case IntLiteralExpr ie:
                    Write(TypeManager.JType.JInt.ToJavaLiteral(ie.Value));
                    break;
                case KeysExpr ke:
                    // Note: the C# runtime produces a `PrtSeq` so we do the same here.  It would save
                    // an allocation if we knew for sure we could simply produce a Set<K>...
                    t = _context.Types.JavaTypeFor(ke.Expr.Type);
                    if (!(t is TypeManager.JType.JMap))
                    {
                        throw new Exception($"Got an unexpected {t.TypeName} rather than a Map");
                    }
                    TypeManager.JType.JMap mt = (TypeManager.JType.JMap)t;

                    Write($"new {mt.KeyCollectionType}(");
                    WriteExpr(ke.Expr);
                    Write($".{mt.KeysMethodName}()");
                    Write($")");
                    break;
                
                case NamedTupleExpr ne:
                    goto default; // TODO
                    break;
                case NondetExpr ne:
                    goto default; // TODO
                    break;
                case NullLiteralExpr ne:
                    Write("null");
                    break;
                case SizeofExpr se:
                    goto default; // TODO
                    break;
                case StringExpr se:
                {
                    string fmtLit = TypeManager.JType.JString.ToJavaLiteral(se.BaseString);
                    if (se.Args.Count == 0)
                    {
                        Write(fmtLit);
                    }
                    else
                    {
                        Write($"MessageFormat.format({fmtLit}");
                        foreach (var arg in se.Args)
                        {
                            Write(", ");
                            WriteExpr(arg);
                        }

                        Write(")");
                    }

                    break;
                }
                case ThisRefExpr te:
                    goto default;
                    break;
                case UnaryOpExpr ue:
                    switch (ue.Operation)
                    {
                        case UnaryOpType.Negate:
                            Write("-");
                            break;
                        case UnaryOpType.Not:
                            Write("!");
                            break;
                                
                    }
                    Write("(");
                    WriteExpr(ue.SubExpr);
                    Write(")");
                    break;
                case UnnamedTupleExpr ue:
                    goto default; // TODO
                    break;
                case ValuesExpr ve:
                    goto default; // TODO
                    break;
                
                case MapAccessExpr _:
                case NamedTupleAccessExpr _:
                case SeqAccessExpr _:
                case TupleAccessExpr _:
                case VariableAccessExpr _:
                    WriteStructureAccess(expr);
                    break;
                
                default:
                    throw new NotImplementedException(expr.GetType().ToString());
            }
        }

        private void WriteBinOp(IPExpr left, BinOpType op, IPExpr right)
        {
            Write("(");
            switch (op)
            {
                // Arithmetic operators
                case BinOpType.Add:
                    WriteExpr(left); Write(" + "); WriteExpr(right);
                    break;
                case BinOpType.Sub:
                    WriteExpr(left); Write(" - "); WriteExpr(right);
                    break;
                case BinOpType.Mul:
                    WriteExpr(left); Write(" * "); WriteExpr(right);
                    break;
                case BinOpType.Div:
                    WriteExpr(left); Write(" / "); WriteExpr(right);
                    break;
                case BinOpType.Mod:
                    WriteExpr(left); Write(" % "); WriteExpr(right);
                    break;
                
                // Comparison operators
                // TODO: Do we need to worry about comparisons between data structures
                // (i.e. for lexiographic equality)?  If so we will need to use Objects.compare()
                // with the correct Comparator object.
                case BinOpType.Lt:
                    WriteExpr(left); Write(" < "); WriteExpr(right);
                    break;
                case BinOpType.Le:
                    WriteExpr(left); Write(" <= "); WriteExpr(right);
                    break;
                case BinOpType.Ge:
                    WriteExpr(left); Write(" >= "); WriteExpr(right);
                    break;
                case BinOpType.Gt:
                    WriteExpr(left); Write(" > "); WriteExpr(right);
                    break;

                // Equality operators (Note: Objects.equals() auto-boxes primitive types for us.)
                case BinOpType.Neq:
                    Write("!");
                    goto case BinOpType.Eq;
                case BinOpType.Eq:
                    Write("Objects.equals(");
                    WriteExpr(left);
                    Write(", ");
                    WriteExpr(right);
                    Write(")");
                    break;
                default:
                    throw new NotImplementedException(op.ToString());
            }
            Write(")");
        }

        private void WriteClone(CloneExpr ce)
        {
            TypeManager.JType t = _context.Types.JavaTypeFor(ce.Term.Type);
            switch (t)
            {
                /* Primitive types are easy since they're copy by value. */
                case TypeManager.JType.JVoid _:
                case TypeManager.JType.JBool _:
                case TypeManager.JType.JInt _:
                case TypeManager.JType.JFloat _:
                    WriteExpr(ce.Term);
                    break;
                    
                /* Same with immutable types. */
                case TypeManager.JType.JString _:
                case TypeManager.JType.JMachine _:
                    WriteExpr(ce.Term);
                    break;
                
                default:
                    throw new Exception($"{ce}.clone() not implemented yet");
            }
        }

        private void WriteStructureAccess(IPExpr e)
        {
            TypeManager.JType t = _context.Types.JavaTypeForVarLocation(e);
            
            // We have to explicitly cast accesses to collections since we might be upcasting (say,
            // if we're extracting an int out of a tuple (List<Object>).).  Use the reference
            // type name to ensure we're casting to another Object (and let Java handle auto-unboxing
            // if it can.)
            switch (e)
            {
                case MapAccessExpr _:
                case NamedTupleAccessExpr _:
                case SeqAccessExpr _:
                case TupleAccessExpr _:
                    Write($"({_context.Types.JavaTypeFor(e.Type).ReferenceTypeName})");
                    break;
            }

            switch (e) {
                case MapAccessExpr mapAccessExpr:
                    WriteExpr(mapAccessExpr.MapExpr);
                    Write($".{t.AccessorMethodName}(");
                    WriteExpr(mapAccessExpr.IndexExpr);
                    Write(")");
                    break;

                case NamedTupleAccessExpr namedTupleAccessExpr:
                    WriteExpr(namedTupleAccessExpr.SubExpr);
                    Write($".{t.AccessorMethodName}(\"{namedTupleAccessExpr.FieldName}\")");
                    break;

                case SeqAccessExpr seqAccessExpr:
                    // TODO: do we need to think about handling out of bounds exceptions?
                    WriteExpr(seqAccessExpr.SeqExpr);
                    Write($".{t.AccessorMethodName}(");
                    WriteExpr(seqAccessExpr.IndexExpr);
                    Write(")");
                    break;

                case TupleAccessExpr tupleAccessExpr:
                    WriteExpr(tupleAccessExpr.SubExpr);
                    Write($".{t.AccessorMethodName}(\"{tupleAccessExpr.FieldNo.ToString()}\")");
                    break;

                case VariableAccessExpr variableAccessExpr:
                    Write(_context.Names.GetNameForDecl(variableAccessExpr.Variable));
                    break;
            }
        }

        private void WriteLine(string s = "")
        {
            _context.WriteLine(_source.Stream, s);
        }

        private void Write(string s)
        {
            _context.Write(_source.Stream, s);
        }
    }
}
