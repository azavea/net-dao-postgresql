// Copyright (c) 2004-2010 Azavea, Inc.
// 
// Permission is hereby granted, free of charge, to any person
// obtaining a copy of this software and associated documentation
// files (the "Software"), to deal in the Software without
// restriction, including without limitation the rights to use,
// copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the
// Software is furnished to do so, subject to the following
// conditions:
// 
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES
// OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT
// HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,
// WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
// FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR
// OTHER DEALINGS IN THE SOFTWARE.

using System;
using System.Collections.Generic;
using System.Text;
using Azavea.Open.DAO.Criteria;
using Azavea.Open.DAO.Criteria.Spatial;
using Azavea.Open.DAO.SQL;
using GeoAPI.Geometries;

namespace Azavea.Open.DAO.PostgreSQL
{
    /// <summary>
    /// Implements a FastDao layer customized for PostGreSQL (optionally with PostGIS installed).
    /// </summary>
    public class PostgreSqlDaLayer : SqlDaDdlLayer
    {
        private readonly EWKTWriter _ewktWriter = new EWKTWriter();
        private readonly EWKTReader _ewktReader = new EWKTReader();
        /// <summary>
        /// Construct the layer.  Should typically be called only by the appropriate
        /// ConnectionDescriptor.
        /// </summary>
        /// <param name="connDesc">Connection to the PostGreSQL / PostGIS DB we'll be using.</param>
        public PostgreSqlDaLayer(PostgreSqlDescriptor connDesc)
            : base(connDesc, true)
        {
            _coerceableTypes = new Dictionary<Type, TypeCoercionDelegate>();
            _coerceableTypes.Add(typeof(IGeometry), CreateGeometry);
        }

        /// <exclude/>
        public override IDaQuery CreateQuery(ClassMapping mapping, DaoCriteria crit)
        {
            // Overridden to handle IGeometry types, and to always fully qualify column names.
            SqlDaQuery retVal = _sqlQueryCache.Get();
            retVal.Sql.Append("SELECT ");
            foreach (string attName in mapping.AllDataColsByObjAttrs.Keys)
            {
                string colName = mapping.AllDataColsByObjAttrs[attName];
                Type colType = mapping.DataColTypesByObjAttr[attName];
                if (typeof(IGeometry).IsAssignableFrom(colType))
                {
                    retVal.Sql.Append("ST_AsEWKT(");
                    retVal.Sql.Append(colName);
                    retVal.Sql.Append(") AS ");
                    retVal.Sql.Append(colName);
                }
                else
                {
                    if (!colName.Contains("."))
                    {
                        retVal.Sql.Append(mapping.Table);
                        retVal.Sql.Append(".");
                    }
                    retVal.Sql.Append(colName);
                }
                retVal.Sql.Append(", ");
            }
            retVal.Sql.Remove(retVal.Sql.Length - 2, 2);
            retVal.Sql.Append(" FROM ");
            retVal.Sql.Append(mapping.Table);
            ExpressionsToQuery(retVal, crit, mapping);
            OrdersToQuery(retVal, crit, mapping);

            // Don't return the query, we'll do that in DisposeOfQuery.
            return retVal;
        }

        /// <summary>
        /// Overriden to convert IGeometries to correctly-encoded strings that PostGIS can recognize.
        /// 
        /// This is called prior to inserting or updating these values in the table.
        /// </summary>
        /// <param name="table">The table these values will be inserted or updated into.</param>
        /// <param name="propValues">A dictionary of "column"/value pairs for the object to insert or update.</param>
        protected override void PreProcessPropertyValues(string table,
                                                         IDictionary<string, object> propValues)
        {
            // Look for objects being saved that are IGeometries.
            IDictionary<string, object> propsToModify = new Dictionary<string, object>();
            foreach (string propName in propValues.Keys)
            {
                if (propValues[propName] is IGeometry)
                {
                    IGeometry theGeom = (IGeometry)propValues[propName];
                    propsToModify.Add(propName, _ewktWriter.Write(theGeom));
                }
            }
            foreach (string propName in propsToModify.Keys)
            {
                propValues[propName] = propsToModify[propName];
            }
        }

        /// <summary>
        /// Creates a geometry object from the string input.
        /// </summary>
        /// <param name="input">An "e" well known text string.</param>
        /// <returns>A geometry object</returns>
        public object CreateGeometry(object input)
        {
            object o = _ewktReader.Read((string)input);
            return o;
        }

        /// <summary>
        /// Converts a single Expression to SQL (mapping the columns as appropriate) and appends
        /// to the given string builder.
        /// 
        /// The expression's SQL will already be wrapped in parends, so you do not need to add them
        /// here.
        /// </summary>
        /// <param name="queryToAddTo">Query we're adding the expression to.</param>
        /// <param name="expr">The expression.  NOTE: It should NOT be null. This method does not check.</param>
        /// <param name="mapping">Class mapping for the class we're dealing with.</param>
        /// <param name="colPrefix">What to prefix column names with, I.E. "Table." for "Table.Column".
        ///                         May be null if no prefix is desired.  May be something other than
        ///                         the table name if the tables are being aliased.</param>
        /// <param name="booleanOperator">The boolean operator (AND or OR) to insert before
        ///                               this expression.  Blank ("") if we don't need one.</param>
        /// <returns>Whether or not this expression modified the sql string.
        ///          Typically true, but may be false for special query types 
        ///          that use other parameters for certain types of expressions.</returns>
        protected override bool ExpressionToQuery(SqlDaQuery queryToAddTo, IExpression expr,
                                                  ClassMapping mapping, string colPrefix, string booleanOperator)
        {
            // All the spatial expressions we support modify the sql.
            bool retVal = true;
            bool trueOrNot = expr.TrueOrNot();
            if (expr is IntersectsExpression)
            {
                queryToAddTo.Sql.Append(booleanOperator);
                IntersectsExpression intersects = (IntersectsExpression)expr;
                queryToAddTo.Sql.Append("ST_Intersects(ST_GeomFromEWKT(?),");
                queryToAddTo.Params.Add(_ewktWriter.Write(intersects.Shape));
                queryToAddTo.Sql.Append(colPrefix).Append(mapping.AllDataColsByObjAttrs[intersects.Property]);
                queryToAddTo.Sql.Append(")");
            }
            else if (expr is WithinExpression)
            {
                queryToAddTo.Sql.Append(booleanOperator);
                WithinExpression within = (WithinExpression)expr;
                queryToAddTo.Sql.Append("ST_Contains(ST_GeomFromEWKT(?),");
                queryToAddTo.Params.Add(_ewktWriter.Write(within.Shape));
                queryToAddTo.Sql.Append(colPrefix).Append(mapping.AllDataColsByObjAttrs[within.Property]);
                queryToAddTo.Sql.Append(")");
            }
            else if (expr is ContainsExpression)
            {
                queryToAddTo.Sql.Append(booleanOperator);
                ContainsExpression contains = (ContainsExpression)expr;
                queryToAddTo.Sql.Append("ST_Contains(");
                queryToAddTo.Sql.Append(colPrefix).Append(mapping.AllDataColsByObjAttrs[contains.Property]);
                queryToAddTo.Sql.Append(",ST_GeomFromEWKT(?)");
                queryToAddTo.Params.Add(_ewktWriter.Write(contains.Shape));
                queryToAddTo.Sql.Append(")");
            }
            else if (expr is AbstractDistanceExpression)
            {
                queryToAddTo.Sql.Append(booleanOperator);
                AbstractDistanceExpression dist = (AbstractDistanceExpression)expr;

                queryToAddTo.Sql.Append("ST_Distance(");
                queryToAddTo.Sql.Append(colPrefix).Append(mapping.AllDataColsByObjAttrs[dist.Property]);
                queryToAddTo.Sql.Append(", ST_GeomFromEWKT(?))");
                queryToAddTo.Params.Add(EWKTWriter.ToPoint((IPoint)dist.Shape));
                if (dist is LesserDistanceExpression)
                {
                    queryToAddTo.Sql.Append(trueOrNot ? " < ?" : " >= ?");
                }
                else if (expr is GreaterDistanceExpression)
                {
                    queryToAddTo.Sql.Append(trueOrNot ? " > ?" : " <= ?");
                }
                else
                {
                    throw new ArgumentException("Distance expression type " +
                                                expr.GetType() + " not supported.", "expr");
                }
                queryToAddTo.Params.Add(dist.Distance);
            }
            else if (expr is AbstractDistanceSphereExpression)
            {
                queryToAddTo.Sql.Append(booleanOperator);
                AbstractDistanceSphereExpression dist = (AbstractDistanceSphereExpression)expr;
                if (!(dist.Shape is IPoint))
                {
                    throw new ArgumentException("Spherical distance from a non-point is not supported.");
                }

                queryToAddTo.Sql.Append("ST_distance_sphere(");
                queryToAddTo.Sql.Append(colPrefix).Append(mapping.AllDataColsByObjAttrs[dist.Property]);
                queryToAddTo.Sql.Append(", ST_GeomFromEWKT(?))");
                queryToAddTo.Params.Add(EWKTWriter.ToPoint((IPoint)dist.Shape));
                if (dist is LesserDistanceSphereExpression)
                {
                    queryToAddTo.Sql.Append(trueOrNot ? " < ?" : " >= ?");
                }
                else if (expr is GreaterDistanceSphereExpression)
                {
                    queryToAddTo.Sql.Append(trueOrNot ? " > ?" : " <= ?");
                }
                else
                {
                    throw new ArgumentException("Distance expression type " +
                                                expr.GetType() + " not supported.", "expr");
                }
                queryToAddTo.Params.Add(dist.Distance);
            }
            else
            {
                // Fall back to the stuff supported by the base class.
                retVal = base.ExpressionToQuery(queryToAddTo, expr, mapping, colPrefix, booleanOperator);
            }
            return retVal;
        }

        #region Implementation of IDaDdlLayer

        /// <summary>
        /// Add the definition for the given column to the create table statement.
        /// </summary>
        /// <param name="sb">Current create table statement to append to.</param>
        /// <param name="col">Name of the column to add a definition for.</param>
        /// <param name="mapping">Classmapping we're creating a table for.</param>
        /// <param name="separator">Separator to use before appending to sb.</param>
        /// <param name="extraStatements">If adding this column requires any additional
        ///                               SQL statements to be run afterwards, put them here.</param>
        /// <returns>Whether or not it appended anything to the string builder.</returns>
        protected override bool AddColDefinition(StringBuilder sb, string col, ClassMapping mapping,
            string separator, ICollection<string> extraStatements)
        {
            Type colType = GetDataType(col, mapping);

            // Geometry types are special.
            if (typeof(IGeometry).IsAssignableFrom(colType))
            {
                string geomType = "GEOMETRY";
                // Specify geometry type if we can.
                if (colType == typeof(IPoint))
                {
                    geomType = "POINT";
                }
                else if (colType == typeof(ILineString))
                {
                    geomType = "LINESTRING";
                }
                else if (colType == typeof(IPolygon))
                {
                    geomType = "POLYGON";
                }
                else if (colType == typeof(IMultiPoint))
                {
                    geomType = "MULTIPOINT";
                }
                else if (colType == typeof(IMultiLineString))
                {
                    geomType = "MULTILINESTRING";
                }
                else if (colType == typeof(IMultiPolygon))
                {
                    geomType = "MULTIPOLYGON";
                }
                extraStatements.Add("SELECT AddGeometryColumn('" + mapping.Table +
                                    "', '" + col + "', -1, '" + geomType + "', 2)");
                return false;
            }
            return base.AddColDefinition(sb, col, mapping, separator, extraStatements);
        }

        /// <summary>
        /// Returns the DDL for the type of an automatically incrementing column.
        /// Some databases only store autonums in one col type so baseType may be
        /// ignored.
        /// </summary>
        /// <param name="baseType">The data type of the column (nominally).</param>
        /// <returns>The autonumber definition string.</returns>
        protected override string GetAutoType(Type baseType)
        {
            return "SERIAL";
        }

        /// <summary>
        /// Returns the SQL type used to store a byte array in the DB.
        /// </summary>
        protected override string GetByteArrayType()
        {
            return "BYTEA";
        }

        /// <summary>
        /// Returns the SQL type used to store a long in the DB.
        /// </summary>
        protected override string GetLongType()
        {
            return "BIGINT";
        }

        /// <summary>
        /// Returns the SQL type used to store a "normal" (unicode) string in the DB.
        /// </summary>
        protected override string GetStringType()
        {
            return "TEXT";
        }

        /// <summary>
        /// PostgreSQL encoding is specified by the database not by the column,
        /// so we just use TEXT again.
        /// </summary>
        /// <returns></returns>
        protected override string GetAsciiStringType()
        {
            return "TEXT";
        }

        /// <summary>
        /// Returns the SQL type used to store a boolean in the DB.
        /// </summary>
        protected override string GetBooleanType()
        {
            return "BOOLEAN";
        }

        #endregion
    }
}