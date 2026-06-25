<?xml version="1.0" encoding="UTF-8"?>
<xsl:stylesheet version="2.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:ct="urn:BimHouse:CommonDataType" xmlns:cfg="urn:BimHouse:BomUtilConfig" xmlns:xsi='http://www.w3.org/2001/XMLSchema-instance' xmlns:bf='urn:BimHouse:XslFunctions'>
	<xsl:import href="file:///E:/ArchAndDesign/BIMObjects/Templates/Sheet%20Templates/CustomXmlParts/BimHouseFunctions.xsl"/>

	<!--<xsl:template match="*[starts-with(@xsi:type,'ct:Тип.Базовый.СписокМатериалов')]" mode="presenting">-->
	<xsl:template match="*[bf:InstanceOf(.,'urn:BimHouse:CommonDataType','Тип.Базовый.СписокРабот')]" mode="presenting">
		<xsl:element name="{name()}" namespace="{namespace-uri()}">
			<xsl:copy-of select="@*"/>
			<xsl:apply-templates select="./node()" mode="#current"/>
			<xsl:call-template name="CreateWorkTextPresenting">
				<xsl:with-param name="WorkList" select="."/>
			</xsl:call-template>
		</xsl:element>
	</xsl:template>

	<xsl:template name="CreateWorkTextPresenting">
		<xsl:param name="WorkList"/>
		<xsl:variable name="WorkNodesCount" select="count($WorkList/ct:Работы/ct:Работа)"/>
		<xsl:variable name="DocRefCount" select="count($WorkList/ct:Работы/ct:Реестр )"/>
		<xsl:choose>
			<xsl:when test="$WorkNodesCount &gt; 0">
				<xsl:element name="Представления" namespace="urn:BimHouse:CommonDataType">
					<xsl:for-each select="$WorkList/ct:Работы/ct:Работа">
						<xsl:sort select="ct:ПорядковыйНомер"/>
						<xsl:variable name="sd" select="ct:ДокументПодтверждающийКачество/ct:ПериодДействия/ct:Начало"/>
						<xsl:variable name="ed" select="ct:ДокументПодтверждающийКачество/ct:ПериодДействия/ct:Конец"/>
						<xsl:element name="ct:Список" namespace="urn:BimHouse:CommonDataType">
							<xsl:element name="ct:Текст" namespace="urn:BimHouse:CommonDataType">
								<xsl:if test="ct:ПорядковыйНомер">
									<xsl:value-of select="ct:ПорядковыйНомер"/>
									<xsl:text>. </xsl:text>
								</xsl:if>
								<xsl:value-of select="ct:Наименование"/>
								<xsl:text> - </xsl:text>
								<xsl:value-of select="ct:Количество"/>
								<xsl:text>(</xsl:text>
								<xsl:value-of select="ct:ЕдиницаИзмерения"/>
								<xsl:text>), в/на </xsl:text>
								<xsl:value-of select="ct:МеcтоВыполнения"/>
							</xsl:element>
							<xsl:element name="ct:НомерРаботаКоличествоМесто" namespace="urn:BimHouse:CommonDataType">
								<xsl:if test="ct:ПорядковыйНомер">
									<xsl:value-of select="ct:ПорядковыйНомер"/>
									<xsl:text>. </xsl:text>
								</xsl:if>
								<xsl:value-of select="ct:Наименование"/>
								<xsl:text> - </xsl:text>
								<xsl:value-of select="ct:Количество"/>
								<xsl:text>(</xsl:text>
								<xsl:value-of select="ct:ЕдиницаИзмерения"/>
								<xsl:text>), в/на </xsl:text>
								<xsl:value-of select="ct:МеcтоВыполнения"/>
							</xsl:element>
							<xsl:element name="ct:РаботаКоличество" namespace="urn:BimHouse:CommonDataType">
								<xsl:value-of select="ct:Наименование"/>
								<xsl:text> - </xsl:text>
								<xsl:value-of select="ct:Количество"/>
								<xsl:text>(</xsl:text>
								<xsl:value-of select="ct:ЕдиницаИзмерения"/>
								<xsl:text>)</xsl:text>
							</xsl:element>
						</xsl:element>
					</xsl:for-each>
				</xsl:element>
			</xsl:when>
			<xsl:when test="$DocRefCount &gt; 0">
				<xsl:element name="Представления" namespace="urn:BimHouse:CommonDataType">
					<xsl:element name="ct:Список" namespace="urn:BimHouse:CommonDataType">
						<xsl:element name="ct:Текст" namespace="urn:BimHouse:CommonDataType">
							<xsl:value-of select="$WorkList/ct:Работы/ct:Реестр/ct:ТипДокумента"/>
							<xsl:text> №&#160;</xsl:text>
							<xsl:value-of select="$WorkList/ct:Работы/ct:Реестр/ct:НомерДокумента"/>
							<xsl:text> от </xsl:text>
							<xsl:value-of select="format-date($WorkList/ct:Работы/ct:Реестр/ct:ДатаДокумента,'[D01].[M01].[Y0001]')"/>
							<xsl:text> </xsl:text>
							<xsl:value-of select="$WorkList/ct:Работы/ct:Реестр/ct:Титул"/>
						</xsl:element>
					</xsl:element>
				</xsl:element>
			</xsl:when>
		</xsl:choose>
	</xsl:template>

</xsl:stylesheet>