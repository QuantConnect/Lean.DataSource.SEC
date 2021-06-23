/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Lean Algorithmic Trading Engine v2.0. Copyright 2014 QuantConnect Corporation.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 *
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ProtoBuf;
using ProtoBuf.Meta;
using Newtonsoft.Json;
using NUnit.Framework;
using QuantConnect.Data;
using QuantConnect.DataSource;

namespace QuantConnect.DataLibrary.Tests
{
    [TestFixture]
    public class SECTests
    {
        [Test]
        public void JsonRoundTrip()
        {
            var expected = CreateNewInstance();
            var type = expected.GetType();
            var serialized = JsonConvert.SerializeObject(expected);
            var result = JsonConvert.DeserializeObject(serialized, type);

            AssertAreEqual(expected, result);
        }

        [Test]
        public void ProtobufRoundTrip()
        {
            var expected = CreateNewInstance();
            var type = expected.GetType();

            RuntimeTypeModel.Default[typeof(BaseData)].AddSubType(2000, type);

            using (var stream = new MemoryStream())
            {
                Serializer.Serialize(stream, expected);

                stream.Position = 0;

                var result = Serializer.Deserialize(type, stream);

                AssertAreEqual(expected, result, filterByCustomAttributes: true);
            }
        }

        [Test]
        public void Clone()
        {
            var expected = CreateNewInstance();
            var result = expected.Clone();

            AssertAreEqual(expected, result);
        }

        private void AssertAreEqual(object expected, object result, bool filterByCustomAttributes = false)
        {
            foreach (var propertyInfo in expected.GetType().GetProperties())
            {
                // we skip Symbol which isn't protobuffed
                if (filterByCustomAttributes && propertyInfo.CustomAttributes.Count() != 0)
                {
                    Assert.AreEqual(propertyInfo.GetValue(expected), propertyInfo.GetValue(result));
                }
            }
            foreach (var fieldInfo in expected.GetType().GetFields())
            {
                Assert.AreEqual(fieldInfo.GetValue(expected), fieldInfo.GetValue(result));
            }
        }

        private BaseData CreateNewInstance()
        {
            var report = new SECReportSubmission 
            {
                AccessionNumber = "ABCDEF00001",
                FormType = "2.1",
                PublicDocumentCount = "5",
                Period = new DateTime(2021, 5, 5),
                Items = new List<string>
                {
                    "2",
                    "2.1",
                    "8",
                    "8.2"
                },
                FilingDate = new DateTime(2021, 4, 1),
                FilingDateChange = new DateTime(2021, 5, 1),
                MadeAvailableAt = new DateTime(2021, 5, 6, 12, 0, 0),
                Filers = new List<SECReportFiler>
                {
                    new SECReportFiler
                    {
                        CompanyData = new SECReportCompanyData
                        {
                            ConformedName = "ABCDEF00001 INC.",
                            Cik = "000000001",
                            AssignedSic = "1000",
                            IrsNumber = "99-999-9999",
                            StateOfIncorporation = "TX",
                            FiscalYearEnd = "0401"
                        },
                        Values = new List<SECReportFilingValues>
                        {
                            new SECReportFilingValues
                            {
                                FormType = "8-K",
                                Act = "11.5c",
                                FileNumber = "000000000",
                                FilmNumber = "123123123"
                            }
                        },
                        BusinessAddress = new List<SECReportBusinessAddress>
                        {
                            new SECReportBusinessAddress
                            {
                                StreetOne = "1234 Sesame St.",
                                StreetTwo = "Ste 5000",
                                City = "Los Angeles",
                                State = "CA",
                                Zip = "90210",
                                Phone = "1-800-555-0100"
                            }
                        },
                        MailingAddress = new List<SECReportMailAddress>(),
                        FormerCompanies = new List<SECReportFormerCompany>()
                    }
                },
                Documents = new List<SECReportDocument>
                {
                    new SECReportDocument
                    {
                        FormType = "8-K",
                        Sequence = 5,
                        Filename = "SEC-8K-0000000-20200101.nc",
                        Description = "ABCDEF00001 Statements Consolidated (Non-GAAP)",
                        Text = "Financial report metrics, coming to you live at 7PM central..."
                    }
                }
            };

            return new SECReport8K(report)
            {
                Symbol = Symbol.Empty,
                Time = DateTime.Today,
                DataType = MarketDataType.Base,
            };
        }
    }
}
