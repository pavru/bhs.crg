<?xml version="1.0" encoding="UTF-8"?>
<xsl:stylesheet
	version='3.0'
	xmlns:xsl='http://www.w3.org/1999/XSL/Transform'
	xmlns:fo='http://www.w3.org/1999/XSL/Format'
	xmlns:ct='urn:BimHouse:CommonDataType'
	xmlns:xsi='http://www.w3.org/2001/XMLSchema-instance'
	xmlns:bf='urn:BimHouse:XslFunctions'
	xmlns:xs='http://www.w3.org/2001/XMLSchema'>
	<xsl:include href='file:///E:/ArchAndDesign/BIMObjects/Templates/Sheet%20Templates/CustomXmlParts/NewElementResolverStyles.xsl'/>
	<xsl:include href='file:///E:/ArchAndDesign/BIMObjects/Templates/Sheet%20Templates/CustomXmlParts/AddressStyles.xsl'/>
	<xsl:include href='file:///E:/ArchAndDesign/BIMObjects/Templates/Sheet%20Templates/CustomXmlParts/PersonStyles.xsl'/>
	<xsl:include href='file:///E:/ArchAndDesign/BIMObjects/Templates/Sheet%20Templates/CustomXmlParts/OrgStyles.xsl'/>
	<xsl:include href='file:///E:/ArchAndDesign/BIMObjects/Templates/Sheet%20Templates/CustomXmlParts/ConstructionObjectStyles.xsl'/>
	<xsl:include href='file:///E:/ArchAndDesign/BIMObjects/Templates/Sheet%20Templates/CustomXmlParts/ListOfMaterialStyles.xsl'/>
	<xsl:include href='file:///E:/ArchAndDesign/BIMObjects/Templates/Sheet%20Templates/CustomXmlParts/DocumentStyles.xsl'/>
	<xsl:output
		method='xml'
		version='1.0'
		encoding='UTF-8'
		indent='yes'/>
	<xsl:template match="processing-instruction()[name() = 'xml-stylesheet']"/>
	<xsl:variable
		name='ThisMatListURI'
		as='xs:anyURI'
		select='document-uri()'/>
	<xsl:template match="*[bf:InstanceOf(.,'urn:BimHouse:CommonDataType','Тип.ФайлОбщихДанных')]" mode='presenting'/>
	<xsl:template match="*[bf:InstanceOf(.,'urn:BimHouse:CommonDataType','Тип.Базовый.Документ.Приложение.ВедомостьМатериалов')]" mode='subtype-presenting'>
		<xsl:element name='ct:Обработки' namespace='urn:BimHouse:CommonDataType'>
			<xsl:element name='ct:ДокументыКачества' namespace='urn:CommonDataType'>
				<xsl:for-each-group select='ct:Материалы/ct:Материал/ct:ДокументПодтверждающийКачество' group-by="lower-case(concat(normalize-space(ct:ТипДокумента),':',ct:НомерДокумента,':',ct:ДатаДокумента))">
					<xsl:copy-of select='current-group()[1]'/>
				</xsl:for-each-group>
			</xsl:element>
		</xsl:element>
	</xsl:template>
	<xsl:template match='ct:Материал' mode='presenting'>
		<xsl:element name='{name()}' namespace='{namespace-uri()}'>
			<xsl:copy-of select='@*'/>
			<xsl:variable name='RichNode'>
				<xsl:apply-templates select='./node()' mode='#current'/>
			</xsl:variable>
			<xsl:copy-of select='./@*'/>
			<xsl:copy-of select='./*'/>
			<xsl:element name='ct:Представления' namespace='urn:BimHouse:CommonDataType'>
				<xsl:element name='ct:НаименованиеСсылка' namespace='urn:BimHouse:CommonDataType'>
					<xsl:value-of select='$RichNode/ct:Наименование'/>
					<xsl:if test='$RichNode/ct:СсылочнаяИнформация'>
						<xsl:text> (</xsl:text>
						<xsl:value-of select='$RichNode/ct:СсылочнаяИнформация'/>
						<xsl:text>)</xsl:text>
					</xsl:if>
				</xsl:element>
				<xsl:element name='ct:ДокументКачества' namespace='urn:BimHouse:CommonDataType'>
					<xsl:for-each select='$RichNode/ct:ДокументПодтверждающийКачество'>
						<xsl:value-of select='ct:ТипДокумента'/>
						<xsl:if test='ct:НомерДокумента'>
							<xsl:text> №&#160;</xsl:text>
							<xsl:value-of select='ct:НомерДокумента'/>
						</xsl:if>
						<xsl:if test='ct:ДатаДокумента'>
							<xsl:text> от </xsl:text>
							<xsl:value-of select="format-date(ct:ДатаДокумента, '[D01].[M01].[Y0001]')"/>
						</xsl:if>
						<xsl:if test="ct:ВыпустившаяОрганизация/ct:Наименование/ct:Краткое != ''">
							<xsl:text>, выдан </xsl:text>
							<xsl:value-of select='ct:ВыпустившаяОрганизация/ct:Наименование/ct:Краткое'/>
						</xsl:if>
						<xsl:if test='ct:Файл'>
							<xsl:text>, (</xsl:text>
							<xsl:value-of select='ct:Файл'/>
							<xsl:text>)</xsl:text>
						</xsl:if>
					</xsl:for-each>
				</xsl:element>
			</xsl:element>
		</xsl:element>
	</xsl:template>
	<!--	<xsl:template match="ct:Материалы[ct:АктКС2]" mode="resolving" priority="10">
		<xsl:element name="ct:Материалы" namespace="urn:BimHouse:CommonDataType">
			<xsl:variable name="MatsFromAllActs">
				<xsl:for-each select="ct:АктКС2">
					<xsl:variable name="Mats">
						<xsl:call-template name="MatFromKs2">
							<xsl:with-param name="Ks2Uri" select="string(ct:АктUri)"/>
							<xsl:with-param name="Ks2Number" select="string(ct:НомерАкта)"/>
							<xsl:with-param name="Exclude" select="ct:ИсключитьМатериалы"/>
							<xsl:with-param name="NameFind" select="ct:ОбработкаНаименования/ct:ШаблонПоиска"/>
							<xsl:with-param name="NameReplace" select="ct:ОбработкаНаименования/ct:ШаблонЗамены"/>
						</xsl:call-template>
					</xsl:variable>
					<xsl:variable name="CertUri" select="string(ct:СертификатUri)"/>
					<xsl:for-each-group select="$Mats/*" group-by="concat(ct:Наименование,', Units: ',ct:ЕдиницаИзмерения)">
						<xsl:element name="ct:Материал" namespace="urn:BimHouse:CommonDataType">
							<xsl:attribute name="xsi:type" select="'ct:Тип.Базовый.Материал'"/>
							<xsl:element name="ct:ПорядковыйНомер" namespace="urn:BimHouse:CommonDataType">
								<xsl:value-of select="position()"/>
							</xsl:element>
							<xsl:element name="ct:Наименование" namespace="urn:BimHouse:CommonDataType">
								<xsl:value-of select="current-group()[1]/ct:Наименование"/>
							</xsl:element>
							<xsl:element name="ct:ЕдиницаИзмерения" namespace="urn:BimHouse:CommonDataType">
								<xsl:value-of select="current-group()[1]/ct:ЕдиницаИзмерения"/>
							</xsl:element>
							<xsl:element name="ct:Количество" namespace="urn:BimHouse:CommonDataType">
								<xsl:value-of select="sum(current-group()/ct:Количество)"/>
							</xsl:element>
							<xsl:element name="ct:СсылочнаяИнформация" namespace="urn:BimHouse:CommonDataType">
								<xsl:choose>
									<xsl:when test="count(current-group()) > 1">
										<xsl:value-of select="string-join(current-group()/ct:СсылочнаяИнформация,',')"/>
									</xsl:when>
									<xsl:otherwise>
										<xsl:value-of select="current-group()[1]/ct:СсылочнаяИнформация"/>
									</xsl:otherwise>
								</xsl:choose>
							</xsl:element>
							<xsl:call-template name="MatCert">
								<xsl:with-param name="MatCertUri" select="$CertUri"/>
								<xsl:with-param name="MatName" select="string(current-group()[1]/ct:Наименование)"/>
							</xsl:call-template>
						</xsl:element>
					</xsl:for-each-group>
				</xsl:for-each>
			</xsl:variable>
			<xsl:for-each select="$MatsFromAllActs/*">
				<xsl:element name="ct:Материал" namespace="urn:BimHouse:CommonDataType">
					<xsl:attribute name="xsi:type" select="'ct:Тип.Базовый.Материал'"/>
					<xsl:variable name="MatPostion" select="position()"/>
					<xsl:for-each select="./*">
						<xsl:choose>
							<xsl:when test="local-name() = 'ПорядковыйНомер'">
								<xsl:element name="ct:ПорядковыйНомер" namespace="urn:BimHouse:CommonDataType">
									<xsl:value-of select="$MatPostion"/>
								</xsl:element>
							</xsl:when>
							<xsl:otherwise>
								<xsl:copy-of select="."/>
							</xsl:otherwise>
						</xsl:choose>
					</xsl:for-each>
				</xsl:element>
			</xsl:for-each>
		</xsl:element>
	</xsl:template>
-->
	<xsl:template
		match='ct:ВедомостьМатериалов/ct:Материалы'
		mode='resolving'
		priority='10'>
		<xsl:choose>
			<xsl:when test='./ct:АктКС2'>
				<xsl:element name='ct:Материалы' namespace='urn:BimHouse:CommonDataType'>
					<xsl:variable name='MatsFromAllActs'>
						<xsl:variable name='CertUri' select='string(ct:АктКС2[1]/ct:СертификатUri)'/>
						<xsl:variable name='Mats'>
							<xsl:for-each select='ct:АктКС2'>
								<xsl:call-template name='MatFromKs2'>
									<xsl:with-param name='Ks2Uri' select='string(ct:АктUri)'/>
									<xsl:with-param name='Ks2Number' select='string(ct:НомерАкта)'/>
									<xsl:with-param name='Exclude' select='ct:ИсключитьМатериалы'/>
									<xsl:with-param name='NameFind' select='ct:ОбработкаНаименования/ct:ШаблонПоиска'/>
									<xsl:with-param name='NameReplace' select='ct:ОбработкаНаименования/ct:ШаблонЗамены'/>
								</xsl:call-template>
							</xsl:for-each>
						</xsl:variable>
						<xsl:for-each-group select='$Mats/*' group-by="concat(ct:Наименование,', Units: ',ct:ЕдиницаИзмерения)">
							<xsl:element name='ct:Материал' namespace='urn:BimHouse:CommonDataType'>
								<xsl:attribute name='xsi:type' select="'ct:Тип.Базовый.Материал'"/>
								<xsl:element name='ct:ПорядковыйНомер' namespace='urn:BimHouse:CommonDataType'>
									<xsl:value-of select='position()'/>
								</xsl:element>
								<xsl:element name='ct:Наименование' namespace='urn:BimHouse:CommonDataType'>
									<xsl:value-of select='current-group()[1]/ct:Наименование'/>
								</xsl:element>
								<xsl:element name='ct:ЕдиницаИзмерения' namespace='urn:BimHouse:CommonDataType'>
									<xsl:value-of select='current-group()[1]/ct:ЕдиницаИзмерения'/>
								</xsl:element>
								<xsl:element name='ct:Количество' namespace='urn:BimHouse:CommonDataType'>
									<xsl:value-of select='sum(current-group()/ct:Количество)'/>
								</xsl:element>
								<xsl:element name='ct:СсылочнаяИнформация' namespace='urn:BimHouse:CommonDataType'>
									<xsl:choose>
										<xsl:when test='count(current-group()) > 1'>
											<xsl:value-of select="string-join(current-group()/ct:СсылочнаяИнформация,',')"/>
										</xsl:when>
										<xsl:otherwise>
											<xsl:value-of select='current-group()[1]/ct:СсылочнаяИнформация'/>
										</xsl:otherwise>
									</xsl:choose>
								</xsl:element>
								<xsl:call-template name='MatCert'>
									<xsl:with-param name='MatCertUri' select='$CertUri'/>
									<xsl:with-param name='MatName' select='string(current-group()[1]/ct:Наименование)'/>
								</xsl:call-template>
							</xsl:element>
						</xsl:for-each-group>
					</xsl:variable>
					<xsl:for-each select='$MatsFromAllActs/*'>
						<xsl:element name='ct:Материал' namespace='urn:BimHouse:CommonDataType'>
							<xsl:attribute name='xsi:type' select="'ct:Тип.Базовый.Материал'"/>
							<xsl:variable name='MatPostion' select='position()'/>
							<xsl:for-each select='./*'>
								<xsl:choose>
									<xsl:when test="local-name() = 'ПорядковыйНомер'">
										<xsl:element name='ct:ПорядковыйНомер' namespace='urn:BimHouse:CommonDataType'>
											<xsl:value-of select='$MatPostion'/>
										</xsl:element>
									</xsl:when>
									<xsl:otherwise>
										<xsl:copy-of select='.'/>
									</xsl:otherwise>
								</xsl:choose>
							</xsl:for-each>
						</xsl:element>
					</xsl:for-each>
				</xsl:element>
			</xsl:when>
			<xsl:otherwise>
				<xsl:element name='ct:Материалы' namespace='urn:BimHouse:CommonDataType'>
					<xsl:copy-of select='./@*'/>
					<!--<xsl:copy-of select='./*'/>-->
					<xsl:apply-templates select="./*" mode="#current"/>
				</xsl:element>
			</xsl:otherwise>
		</xsl:choose>
	</xsl:template>
	<xsl:template name='MatFromKs2'>
		<xsl:param name='Ks2Uri'/>
		<xsl:param name='Ks2Number'/>
		<xsl:param name='Exclude'/>
		<xsl:param name='NameFind'/>
		<xsl:param name='NameReplace'/>
		<xsl:variable name='GrandDoc' select='document(bf:CheckAndCorrectUri($Ks2Uri,$ThisMatListURI))'/>
		<xsl:variable name='ActNumber' select='string($Ks2Number)'/>
		<xsl:variable name='ActIndex' select='string($GrandDoc//ImplemActs/ImplemAct[@Number = $ActNumber]/@ActIndex)'/>
		<!--		<xsl:for-each select="$GrandDoc//Chapters/Chapter/Position[Implementation_V2/Item/@ActIndex = $ActIndex and number(replace(Implementation_V2/Item/Quantity/@Result,',','.')) &gt; 0]">-->
		<xsl:for-each select='$GrandDoc//Chapters/Chapter/Position[Implementation_V2/Item/@ActIndex = $ActIndex]'>
			<xsl:variable name='Caption' select='@Caption'/>
			<xsl:variable name='Code' select='@Code'/>
			<xsl:variable name='Quantity'>
				<xsl:choose>
					<xsl:when test="string(number(replace(Implementation_V2/Item[@ActIndex = $ActIndex]/Quantity/@Result,',','.'))) = 'NaN'">0</xsl:when>
					<xsl:when test='.[Implementation_V2/Item/@ActIndex = $ActIndex]/Implementation_V2/Item/Quantity/@Result'>
						<xsl:value-of select="number(replace(Implementation_V2/Item[@ActIndex = $ActIndex]/Quantity/@Result,',','.'))"/>
					</xsl:when>
					<xsl:otherwise>0</xsl:otherwise>
				</xsl:choose>
			</xsl:variable>
			<xsl:if test='$Quantity &gt; 0'>
				<xsl:if test="not(PriceBase/@OZ) and not(PriceBase/@EM) and PriceBase/@MT and not($Exclude[ct:Обоснование = $Code]) and not($Exclude[ct:Наименование = $Caption]) and not(contains(@Options,'Inactive'))">
					<xsl:element name='ct:Материал' namespace='urn:BimHouse:CommonDataType'>
						<xsl:attribute name='xsi:type' select="'ct:Тип.Базовый.Материал'"/>
						<xsl:element name='ct:Наименование' namespace='urn:BimHouse:CommonDataType'>
							<xsl:choose>
								<xsl:when test='$NameFind and $NameReplace'>
									<xsl:value-of select='replace(@Caption,$NameFind,$NameReplace)'/>
								</xsl:when>
								<xsl:otherwise>
									<xsl:value-of select='@Caption'/>
								</xsl:otherwise>
							</xsl:choose>
						</xsl:element>
						<xsl:element name='ct:ЕдиницаИзмерения' namespace='urn:BimHouse:CommonDataType'>
							<xsl:value-of select='@Units'/>
						</xsl:element>
						<xsl:element name='ct:Количество' namespace='urn:BimHouse:CommonDataType'>
							<xsl:value-of select='$Quantity'/>
						</xsl:element>
						<xsl:element name='ct:СсылочнаяИнформация' namespace='urn:BimHouse:CommonDataType'>КС2#<xsl:value-of select='$ActNumber'/>:<xsl:value-of select='position()'/></xsl:element>
					</xsl:element>
				</xsl:if>
				<xsl:if test="not(PriceCurr/@OZ) and not(PriceCurr/@EM) and PriceCurr/@MT and not($Exclude[ct:Обоснование = $Code]) and not($Exclude[ct:Наименование = $Caption]) and not(contains(@Options,'Inactive'))">
					<xsl:element name='ct:Материал' namespace='urn:BimHouse:CommonDataType'>
						<xsl:attribute name='xsi:type' select="'ct:Тип.Базовый.Материал'"/>
						<xsl:element name='ct:Наименование' namespace='urn:BimHouse:CommonDataType'>
							<xsl:choose>
								<xsl:when test='$NameFind and $NameReplace'>
									<xsl:value-of select='replace(@Caption,$NameFind,$NameReplace)'/>
								</xsl:when>
								<xsl:otherwise>
									<xsl:value-of select='@Caption'/>
								</xsl:otherwise>
							</xsl:choose>
						</xsl:element>
						<xsl:element name='ct:ЕдиницаИзмерения' namespace='urn:BimHouse:CommonDataType'>
							<xsl:value-of select='@Units'/>
						</xsl:element>
						<xsl:element name='ct:Количество' namespace='urn:BimHouse:CommonDataType'>
							<xsl:value-of select='$Quantity'/>
						</xsl:element>
						<xsl:element name='ct:СсылочнаяИнформация' namespace='urn:BimHouse:CommonDataType'>КС2#<xsl:value-of select='$ActNumber'/>:<xsl:value-of select='position()'/></xsl:element>
					</xsl:element>
				</xsl:if>
			</xsl:if>
		</xsl:for-each>
	</xsl:template>
	<xsl:template name='MatCert'>
		<xsl:param name='MatCertUri'/>
		<xsl:param name='MatName'/>
		<xsl:variable name='Doc' select='document(bf:CheckAndCorrectUri($MatCertUri,$ThisMatListURI))/ct:КаталогДокументовПодтверждающихКачество/ct:Группа/ct:Материал[ct:Наименование = $MatName][1]/ct:ДокументПодтверждающийКачество'/>
		<xsl:choose>
			<xsl:when test='not($Doc)'>
				<xsl:element name='ct:ДокументПодтверждающийКачество' namespace='urn:BimHouse:CommonDataType'>
					<xsl:attribute name='xsi:type' select="'ct:Тип.Базовый.Документ'"/>
					<xsl:element name='ct:Ошибка' namespace='urn:BimHouse:CommonDataType'>
						<xsl:text>Документы материала не найдены</xsl:text>
					</xsl:element>
				</xsl:element>
				<xsl:message>
					<xsl:text>Материал </xsl:text>
					<xsl:value-of select='$MatName'/>
					<xsl:text> - сертификат не найден</xsl:text>
				</xsl:message>
			</xsl:when>
			<xsl:otherwise>
				<xsl:copy-of select='$Doc' copy-namespaces='no'/>
			</xsl:otherwise>
		</xsl:choose>
	</xsl:template>
</xsl:stylesheet>