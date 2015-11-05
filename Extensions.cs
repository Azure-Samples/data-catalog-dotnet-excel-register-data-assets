//Microsoft Data Catalog team sample

using System;
using System.Collections.Generic;
using System.Linq;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using System.Data;
using System.IO;

namespace GetStartedADCExtensions
{
    public static class OpenXmlExt
    {
        public static void ExcelTableToDataTable(this DataTable dt, string path, string sheetName, string tableName)
        {
            dt.TableName  = tableName;

            using (SpreadsheetDocument document = SpreadsheetDocument.Open(path, true))
            {
                //References to the workbook and Shared String Table.
                var id = document.WorkbookPart.Workbook.Descendants<Sheet>().First(s => s.Name == sheetName).Id;

                //Get sheet my the sheet id
                WorksheetPart sheet = (WorksheetPart)document.WorkbookPart.GetPartById(id);

                //Get Excel table definition
                TableDefinitionPart tableDefinitionPart = (from t in sheet.TableDefinitionParts where t.Table.DisplayName == tableName select t).First();

                //Get the cell reference for the Excel table
                string cellReference = tableDefinitionPart.Table.Reference.ToString();

                //Get start and end Row and Column
                Regex regexCellName = new Regex("[A-Za-z]+");
                Regex regexRowIndex = new Regex(@"\d+");

                int startRow = Convert.ToInt32(regexRowIndex.Match(OpenXmlExt.ReferenceIndex(cellReference, 0)).ToString());
                int startColumn = Convert.ToInt32(OpenXmlExt.TranslateColumnNameToIndex(regexCellName.Match(OpenXmlExt.ReferenceIndex(cellReference, 0)).ToString()));

                int endRow = Convert.ToInt32(regexRowIndex.Match(OpenXmlExt.ReferenceIndex(cellReference, 1)).ToString());
                int endColumn = Convert.ToInt32(OpenXmlExt.TranslateColumnNameToIndex(regexCellName.Match(OpenXmlExt.ReferenceIndex(cellReference, 1)).ToString()));

                //Get column names
                var columnNames = from n in tableDefinitionPart.Table.TableColumns
                                  select (from a in XDocument.Load(new StringReader(n.OuterXml)).Descendants()
                                          select a.Attribute(XName.Get("name")).Value).First();

                //Convert Excel table to ADO.NET DataTable
                DataColumn dataColumn;
                foreach (var name in columnNames)
                {
                    dataColumn = new DataColumn(name.Replace(" ", string.Empty), typeof(System.String));
                    dataColumn.Caption = name;
                    dt.Columns.Add(dataColumn);
                }

                SharedStringTable sharedStrings = document.WorkbookPart.SharedStringTablePart.SharedStringTable;

                IEnumerable<Row> dataRows =
                    from row in sheet.Worksheet.Descendants<Row>()
                    select row;

                DataRow dataRow;

                foreach (Row row in dataRows)
                {
                    if (row.RowIndex > startRow && row.RowIndex <= endRow)
                    {
                        var cells = from cell in row.Descendants<Cell>() select cell;

                        int rowIndex;
                        int columnIndex;
                        string cellValue;
                        int absoluteColumnIndex;

                        dataRow = dt.NewRow();
                        foreach (var cell in cells)
                        {
                            rowIndex = Convert.ToInt32(regexRowIndex.Match(cell.CellReference.Value).ToString());
                            columnIndex = OpenXmlExt.TranslateColumnNameToIndex(regexCellName.Match(cell.CellReference.Value).ToString());

                            absoluteColumnIndex = columnIndex - startColumn;

                            if (columnIndex >= startColumn && columnIndex <= endColumn)
                            {
                                if (cell.CellValue != null)
                                {
                                    cellValue = cell.CellValue.InnerText;

                                    if (cell.DataType == "s")
                                        cellValue = sharedStrings.ElementAt(Convert.ToInt32(cellValue)).InnerText;

                                    dataRow[absoluteColumnIndex] = cellValue;
                                }
                            }
                        }

                        dt.Rows.Add(dataRow);
                    }
                }

            }
        }

        public static string ReferenceIndex(string cellName, int index)
        {
            return cellName.Split(new char[] { ':' })[index].ToString();
        }

        public static int TranslateColumnNameToIndex(string name)
        {
            int position = 0;

            var chars = name.ToUpperInvariant().ToCharArray().Reverse().ToArray();
            for (var index = 0; index < chars.Length; index++)
            {
                var c = chars[index] - 64;
                position += index == 0 ? c : (c * (int)Math.Pow(26, index));
            }

            return position;
        }
    }
}
